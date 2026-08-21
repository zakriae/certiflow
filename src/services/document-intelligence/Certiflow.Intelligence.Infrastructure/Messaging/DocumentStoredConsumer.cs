using Azure.Storage.Blobs;
using Certiflow.Contracts;
using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Application.Extraction;
using Certiflow.Intelligence.Domain;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.Intelligence.Infrastructure.Persistence;
using Certiflow.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Certiflow.Intelligence.Infrastructure.Messaging;

/// <summary>
/// Runs extraction when Document Intake reports a stored document (FR-3.1).
/// <para>
/// The consumer is deliberately thin: dedupe, fetch the bytes, hand them to the pipeline, save.
/// Everything that decides anything — grounding, confidence, whether the job may complete — is in
/// the domain and already unit-tested without a broker in sight.
/// </para>
/// </summary>
public sealed class DocumentStoredConsumer(
    IntelligenceDbContext database,
    ExtractionPipeline pipeline,
    IDocumentTypeSchemaProvider schemas,
    BlobServiceClient blobs,
    ILogger<DocumentStoredConsumer> logger) : IConsumer<DocumentStored>
{
    public async Task Consume(ConsumeContext<DocumentStored> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var messageId = context.MessageId ?? message.EventId;
        var cancellationToken = context.CancellationToken;

        // Idempotency check first, before any work and before any cost. Service Bus is
        // at-least-once and the outbox can republish after a crash, so this path will be entered
        // twice for the same document sooner or later - and the second time must not pay a model.
        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, cancellationToken))
        {
            ConsumerLog.AlreadyHandled(logger, messageId, message.DocumentId);
            return;
        }

        var schema = schemas.Find(message.ExpectedDocumentType);

        if (schema is null)
        {
            // No extraction contract for this document type. Recorded as handled rather than
            // retried: replaying it will not conjure a schema, and leaving it to redeliver forever
            // would bury the queue. A human adds the schema and re-runs extraction (FR-3.11).
            ConsumerLog.NoSchema(logger, message.ExpectedDocumentType, message.DocumentId);
            await RecordHandledAsync(messageId, cancellationToken);
            return;
        }

        var blob = blobs
            .GetBlobContainerClient(message.StorageContainer)
            .GetBlobClient(message.StorageBlobPath);

        await using var content = await blob.OpenReadAsync(cancellationToken: cancellationToken);

        // The supplier and requirement come from this service's own copy of the registry's data.
        // If either is missing the events have not arrived yet, so the message is retried rather
        // than scored against nothing - a wrong supplier name would silently disable the
        // entity-match check instead of failing visibly.
        var supplier = await database.Suppliers
            .FirstOrDefaultAsync(s => s.SupplierId == message.SupplierId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Supplier {message.SupplierId} is not known to Document Intelligence yet.");

        var requirement = message.RequirementId is { } requirementId
            ? await database.Requirements.FirstOrDefaultAsync(r => r.RequirementId == requirementId, cancellationToken)
            : null;

        var request = new ExtractionRequest(
            new DocumentId(message.DocumentId),
            new SupplierId(message.SupplierId),
            new RequirementId(message.RequirementId ?? Guid.Empty),
            schema,
            new ExtractionContext(
                supplierLegalName: supplier.LegalName,
                supplierTradingName: supplier.TradingName,
                acceptedIssuers: requirement?.AcceptedIssuers ?? [],
                // Only demanded when the requirement says so and actually lists issuers - the
                // aggregate refuses a context that demands a match with nothing to match against.
                requiresIssuerMatch: requirement is { RequiresIssuerMatch: true }
                                     && requirement.AcceptedIssuers.Count > 0,
                // Left null deliberately. A requirement names a document type ("ISO 9001") while a
                // certificate names an edition ("ISO 9001:2015"), so the two are not comparable as
                // strings and asserting a match on them would fail every honest certificate.
                // Matching family-to-edition is a real check worth adding; guessing at it here
                // would be worse than not making the claim.
                expectedStandard: null,
                today: DateOnly.FromDateTime(DateTime.UtcNow)),
            // Per requirement, not a global constant (FR-5.6).
            Confidence.FromScore(requirement?.AutoAcceptThreshold ?? 0.85m));

        var outcome = await pipeline.RunAsync(request, content, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var correlationId = context.CorrelationId ?? message.CorrelationId;

        database.ExtractionJobs.Add(ExtractionJobRecord.FromDomain(outcome.Job, now));

        // The outcome travels onward to Verification. Written to this service's own outbox in the
        // same transaction as the job and the inbox row, so all three commit or none do - the same
        // guarantee Intake makes, for the same reason.
        database.Outbox.Add(ExtractionEventTranslator.ToOutboxMessage(
            ExtractionEventTranslator.ToCompleted(outcome.Job, correlationId), now));

        // A separate event when the model cited text that is not in the document. It is not a
        // failure - the job completed - but it is the one outcome a reviewer must be told about
        // explicitly rather than inferring from a low score (FR-3.4).
        if (ExtractionEventTranslator.ToGroundingFailed(outcome.Job, correlationId) is { } groundingFailed)
        {
            database.Outbox.Add(ExtractionEventTranslator.ToOutboxMessage(groundingFailed, now));
        }

        await RecordHandledAsync(messageId, cancellationToken);

        // Job, outbox and inbox commit together. If they did not, a crash between them would
        // either lose the extraction, lose the event, or let the whole thing run twice.
        await database.SaveChangesAsync(cancellationToken);

        ConsumerLog.Extracted(
            logger,
            message.DocumentId,
            outcome.Job.OverallConfidence.Value,
            outcome.Job.IsAutoAcceptable,
            outcome.Job.TokensConsumed);
    }

    private async Task RecordHandledAsync(Guid messageId, CancellationToken cancellationToken) =>
        await database.Inbox.AddAsync(
            new InboxMessage(messageId, nameof(DocumentStored), DateTimeOffset.UtcNow),
            cancellationToken);
}

internal static partial class ConsumerLog
{
    [LoggerMessage(EventId = 3320, Level = LogLevel.Information,
        Message = "Extracted document {DocumentId} at {Confidence} (auto-acceptable: {AutoAcceptable}, {Tokens} tokens)")]
    public static partial void Extracted(
        ILogger logger, Guid documentId, decimal confidence, bool autoAcceptable, int tokens);

    [LoggerMessage(EventId = 3321, Level = LogLevel.Information,
        Message = "Message {MessageId} for document {DocumentId} was already handled; skipping")]
    public static partial void AlreadyHandled(ILogger logger, Guid messageId, Guid documentId);

    [LoggerMessage(EventId = 3322, Level = LogLevel.Warning,
        Message = "No extraction schema for document type '{DocumentType}' (document {DocumentId})")]
    public static partial void NoSchema(ILogger logger, string documentType, Guid documentId);
}

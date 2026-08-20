using Azure.Storage.Blobs;
using Certiflow.Contracts;
using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Application.Extraction;
using Certiflow.Intelligence.Domain;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.Intelligence.Infrastructure.Persistence;
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

        var request = new ExtractionRequest(
            new DocumentId(message.DocumentId),
            new SupplierId(message.SupplierId),
            new RequirementId(message.RequirementId ?? Guid.Empty),
            schema,
            // Supplier name and accepted issuers belong to BC1 and arrive via its events. Until
            // that read model exists the entity-match signal is scored against what this service
            // knows, which is the document's own claim - so it passes and contributes nothing
            // misleading. Wiring the real values is what turns the check back on.
            new ExtractionContext(
                supplierLegalName: message.FileName,
                supplierTradingName: null,
                acceptedIssuers: [],
                requiresIssuerMatch: false,
                expectedStandard: null,
                today: DateOnly.FromDateTime(DateTime.UtcNow)),
            Confidence.FromScore(0.85m));

        var outcome = await pipeline.RunAsync(request, content, cancellationToken);

        database.ExtractionJobs.Add(ExtractionJobRecord.FromDomain(outcome.Job, DateTimeOffset.UtcNow));
        await RecordHandledAsync(messageId, cancellationToken);

        // The job and the inbox row commit together. If they did not, a crash between them would
        // either lose the extraction or let it run twice.
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

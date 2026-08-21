using Certiflow.Contracts;
using Certiflow.Persistence;
using Certiflow.Verification.Application.Review;
using Certiflow.Verification.Infrastructure.Persistence;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Verification.Infrastructure.Messaging;

/// <summary>
/// Raises a review task when Document Intelligence finishes an extraction (FR-4.1).
/// <para>
/// The point in the system where a machine reading becomes something a person is accountable for.
/// </para>
/// </summary>
public sealed class ExtractionCompletedConsumer(
    VerificationDbContext database,
    ISender sender) : IConsumer<ExtractionCompleted>
{
    public async Task Consume(ConsumeContext<ExtractionCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var messageId = context.MessageId ?? message.EventId;
        var cancellationToken = context.CancellationToken;

        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, cancellationToken))
        {
            return;
        }

        // A field whose citation could not be located is the strongest signal a human is needed:
        // the model produced a value it did not read. Detected from the grounding result rather
        // than by waiting for a separate GroundingFailed event, so the reason is on the task the
        // reviewer opens rather than in a second message that may arrive later.
        var hadGroundingFailure = message.Fields.Any(field =>
            string.Equals(field.GroundingResult, "NotFoundInSource", StringComparison.OrdinalIgnoreCase));

        await sender.Send(
            new RaiseReviewTaskCommand(
                message.DocumentId,
                message.ExtractionJobId,
                message.SupplierId,
                message.RequirementId,
                message.DocumentType,
                // The real uploader, from Intake's DocumentStored. Falling back to a placeholder
                // would quietly disable segregation of duties, so a missing record throws and the
                // message retries until DocumentStored has been processed.
                UploadedBy: (await database.Documents
                    .FirstOrDefaultAsync(d => d.DocumentId == message.DocumentId, cancellationToken))?.UploadedBy
                    ?? throw new InvalidOperationException(
                        $"Document {message.DocumentId} has not been recorded by Verification yet."),
                message.OverallConfidence,
                message.AutoAcceptable,
                hadGroundingFailure,
                [.. message.Fields.Select(ToInput)]),
            cancellationToken);

        database.Inbox.Add(new InboxMessage(messageId, nameof(ExtractionCompleted), DateTimeOffset.UtcNow));

        await database.SaveChangesAsync(cancellationToken);
    }

    private static FieldSuggestionInput ToInput(ExtractedFieldDescriptor field) => new(
        field.FieldName,
        // The typed value where the pipeline could produce one, the raw value otherwise. A
        // reviewer should see the normalised date when there is one and the model's own words
        // when there is not.
        field.TypedValue ?? field.RawValue,
        field.Confidence,
        field.IsMandatory,
        field.CitationPage,
        field.CitationSnippet,
        ScoringNote: field.GroundingResult == "Verified" ? null : $"Grounding: {field.GroundingResult}");
}

/// <summary>
/// Records document metadata so a review task can be raised against the real uploader.
/// </summary>
public sealed class DocumentStoredVerificationConsumer(VerificationDbContext database)
    : IConsumer<DocumentStored>
{
    public async Task Consume(ConsumeContext<DocumentStored> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        if (await database.Documents.AnyAsync(d => d.DocumentId == message.DocumentId, cancellationToken))
        {
            return;
        }

        database.Documents.Add(new DocumentRecord(
            message.DocumentId, message.SupplierId, message.FileName, message.UploadedBy, DateTimeOffset.UtcNow));

        await database.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Cancels an open review task when its document is superseded (FR-4.9).
/// <para>
/// Without this, a reviewer can approve a document that has already been replaced and BC5 records
/// evidence from a stale file.
/// </para>
/// </summary>
public sealed class DocumentSupersededConsumer(
    VerificationDbContext database,
    ISender sender) : IConsumer<DocumentSuperseded>
{
    public async Task Consume(ConsumeContext<DocumentSuperseded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var messageId = context.MessageId ?? context.Message.EventId;

        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, context.CancellationToken))
        {
            return;
        }

        await sender.Send(
            new CancelReviewTaskCommand(
                context.Message.SupersededDocumentId,
                $"Superseded by document {context.Message.SupersedingDocumentId}."),
            context.CancellationToken);

        database.Inbox.Add(new InboxMessage(messageId, nameof(DocumentSuperseded), DateTimeOffset.UtcNow));

        await database.SaveChangesAsync(context.CancellationToken);
    }
}

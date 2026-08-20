using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Application.Evidence;
using Certiflow.Compliance.Application.Suppliers;
using Certiflow.Compliance.Infrastructure.Persistence;
using Certiflow.Contracts;
using Certiflow.Persistence;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Compliance.Infrastructure.Messaging;

/// <summary>
/// Shared plumbing for BC5's consumers: dedupe on the message id, run the command, record the
/// message as handled, and commit both together.
/// </summary>
public abstract class ComplianceConsumerBase<TMessage>(ComplianceDbContext database, ISender sender)
    : IConsumer<TMessage>
    where TMessage : class, IIntegrationEvent
{
    protected ISender Sender => sender;

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var messageId = context.MessageId ?? context.Message.EventId;
        var cancellationToken = context.CancellationToken;

        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, cancellationToken))
        {
            return;
        }

        await HandleAsync(context.Message, cancellationToken);

        database.Inbox.Add(new InboxMessage(messageId, typeof(TMessage).Name, DateTimeOffset.UtcNow));

        // The inbox row commits with whatever the command changed, because the command's own
        // handler shares this DbContext through the scoped unit of work.
        await database.SaveChangesAsync(cancellationToken);
    }

    protected abstract Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// <b>The event that makes evidence count.</b> A reviewer approved a document, so BC5 records it as
/// evidence and re-derives the supplier's status (FR-5.1).
/// </summary>
public sealed class DocumentApprovedConsumer(ComplianceDbContext database, ISender sender)
    : ComplianceConsumerBase<DocumentApproved>(database, sender)
{
    protected override Task HandleAsync(DocumentApproved message, CancellationToken cancellationToken) =>
        Sender.Send(
            new RecordApprovedEvidenceCommand(
                message.SupplierId,
                message.RequirementId,
                message.DocumentId,
                message.CertificateNumber,
                message.IssuerName,
                message.HolderName,
                message.IssuedOn,
                message.ExpiresOn,
                message.ApprovedBy,
                message.ApprovedAt),
            cancellationToken);
}

/// <summary>
/// A document was submitted, so the obligation reads as AwaitingReview rather than Missing while a
/// reviewer works through the queue.
/// </summary>
public sealed class DocumentStoredComplianceConsumer(ComplianceDbContext database, ISender sender)
    : ComplianceConsumerBase<DocumentStored>(database, sender)
{
    protected override async Task HandleAsync(DocumentStored message, CancellationToken cancellationToken)
    {
        if (message.RequirementId is not { } requirementId)
        {
            // An unbound upload has no obligation to move. Not an error - the supplier simply has
            // not said which requirement it satisfies yet.
            return;
        }

        await Sender.Send(
            new RecordSubmissionCommand(message.SupplierId, requirementId, message.DocumentId),
            cancellationToken);
    }
}

/// <summary>
/// A submission ended without approval. The obligation falls back to whatever its existing evidence
/// says — which may still be Satisfied, because a failed renewal does not invalidate the
/// certificate currently in force.
/// </summary>
public sealed class DocumentRejectedConsumer(ComplianceDbContext database, ISender sender)
    : ComplianceConsumerBase<DocumentRejected>(database, sender)
{
    protected override Task HandleAsync(DocumentRejected message, CancellationToken cancellationToken) =>
        Sender.Send(new ClearSubmissionCommand(message.SupplierId, message.RequirementId), cancellationToken);
}

/// <summary>Creates compliance state for a newly registered supplier (BC1 → BC5).</summary>
public sealed class SupplierRegisteredConsumer(ComplianceDbContext database, ISender sender)
    : ComplianceConsumerBase<SupplierRegistered>(database, sender)
{
    protected override Task HandleAsync(SupplierRegistered message, CancellationToken cancellationToken) =>
        Sender.Send(new RegisterSupplierComplianceCommand(message.SupplierId, message.CategoryId), cancellationToken);
}

/// <summary>Rebuilds obligations for every supplier in a category from a new profile version.</summary>
public sealed class ProfileVersionPublishedConsumer(ComplianceDbContext database, ISender sender)
    : ComplianceConsumerBase<ComplianceProfileVersionPublished>(database, sender)
{
    protected override Task HandleAsync(
        ComplianceProfileVersionPublished message,
        CancellationToken cancellationToken) =>
        Sender.Send(
            new ApplyProfileVersionCommand(
                message.CategoryId,
                message.ProfileVersion,
                [.. message.Requirements.Select(r => new RequirementDefinition(
                    r.RequirementId, r.DocumentType, r.IsMandatory, r.RenewalLeadTimeDays, r.MinValidityDays))]),
            cancellationToken);
}

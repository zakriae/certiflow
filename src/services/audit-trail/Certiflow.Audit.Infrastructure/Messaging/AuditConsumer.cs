using System.Text.Json;
using Certiflow.Audit.Domain;
using Certiflow.Audit.Infrastructure.Persistence;
using Certiflow.Contracts;
using Certiflow.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Audit.Infrastructure.Messaging;

/// <summary>
/// Appends one integration event to the ledger (FR-8.1).
/// <para>
/// Generic over the event type, and registered once per contract. Explicit registration rather than
/// a catch-all on <c>IIntegrationEvent</c> is deliberate: the list of audited events is then
/// something a reader can see and an auditor can check, instead of a behaviour that silently
/// depends on how the broker binds interface exchanges.
/// </para>
/// <para>
/// <b>Appends must be serialised.</b> Each entry's id and hash come from its predecessor, so two
/// concurrent appends would fork the chain. This service therefore consumes with a single
/// concurrent handler, backstopped by the primary key on <c>entry_id</c> — a second writer loses on
/// insert rather than producing a chain that verifies but is wrong.
/// </para>
/// </summary>
public sealed class AuditConsumer<TEvent>(AuditDbContext database) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task Consume(ConsumeContext<TEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var messageId = context.MessageId ?? message.EventId;
        var cancellationToken = context.CancellationToken;

        // Redelivery must not append a second copy. An audit trail that grows a duplicate every
        // time a message replays is worse than one that misses an entry: it looks authoritative
        // and is not.
        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, cancellationToken))
        {
            return;
        }

        var previous = await database.Entries
            .OrderByDescending(e => e.EntryId)
            .FirstOrDefaultAsync(cancellationToken);

        var entry = AuditEntry.Append(
            previous?.ToDomain(),
            message.OccurredAt,
            actor: ActorOf(message),
            action: typeof(TEvent).Name,
            entityType: EntityTypeOf(message),
            entityId: EntityIdOf(message).ToString(),
            correlationId: message.CorrelationId,
            payloadJson: JsonSerializer.Serialize(message, PayloadJson));

        database.Entries.Add(AuditEntryRecord.FromDomain(entry));
        database.Inbox.Add(new InboxMessage(messageId, typeof(TEvent).Name, DateTimeOffset.UtcNow));

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Who is responsible. Read from the event where it names a person, "system" otherwise —
    /// an expiry sweep really is the system acting, and pretending otherwise would put a name
    /// against a decision nobody made.
    /// </summary>
    private static string ActorOf(TEvent message) => message switch
    {
        DocumentApproved approved => approved.ApprovedBy,
        DocumentRejected rejected => rejected.RejectedBy,
        DocumentStored stored => stored.UploadedBy,
        FieldCorrected corrected => corrected.CorrectedBy,
        ReviewTaskAssigned assigned => assigned.AssignedTo,
        ReportGenerated report => report.RequestedBy,
        _ => "system",
    };

    /// <summary>
    /// What the entry is about, so the audit view can be filtered by supplier or document (FR-8.4).
    /// </summary>
    private static string EntityTypeOf(TEvent message) => message switch
    {
        DocumentStored or DocumentQuarantined or DocumentSuperseded => "Document",
        ExtractionCompleted or ExtractionFailed or GroundingFailed => "ExtractionJob",
        ReviewTaskRaised or ReviewTaskAssigned or ReviewTaskCancelled or FieldCorrected => "ReviewTask",
        DocumentApproved or DocumentRejected => "Document",
        SupplierRegistered or SupplierActivated or SupplierSuspended or SupplierCategoryChanged => "Supplier",
        ComplianceStatusChanged or CertificateExpiringSoon or CertificateExpired => "Supplier",
        ComplianceProfileVersionPublished => "ComplianceProfile",
        _ => "Unknown",
    };

    private static Guid EntityIdOf(TEvent message) => message switch
    {
        DocumentStored stored => stored.DocumentId,
        DocumentQuarantined quarantined => quarantined.DocumentId,
        DocumentSuperseded superseded => superseded.SupersededDocumentId,
        ExtractionCompleted extraction => extraction.ExtractionJobId,
        ExtractionFailed failed => failed.ExtractionJobId,
        GroundingFailed grounding => grounding.ExtractionJobId,
        ReviewTaskRaised raised => raised.ReviewTaskId,
        ReviewTaskAssigned assigned => assigned.ReviewTaskId,
        ReviewTaskCancelled cancelled => cancelled.ReviewTaskId,
        FieldCorrected corrected => corrected.ReviewTaskId,
        DocumentApproved approved => approved.DocumentId,
        DocumentRejected rejected => rejected.DocumentId,
        SupplierRegistered registered => registered.SupplierId,
        SupplierActivated activated => activated.SupplierId,
        SupplierSuspended suspended => suspended.SupplierId,
        SupplierCategoryChanged changed => changed.SupplierId,
        ComplianceStatusChanged status => status.SupplierId,
        CertificateExpiringSoon expiring => expiring.SupplierId,
        CertificateExpired expired => expired.SupplierId,
        ComplianceProfileVersionPublished published => published.CategoryId,
        ReportGenerated report => report.ReportId,
        _ => Guid.Empty,
    };
}

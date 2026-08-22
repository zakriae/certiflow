using Certiflow.Notifications.Application.Abstractions;
using Certiflow.Notifications.Application.Delivery;
using Certiflow.Notifications.Domain;
using Certiflow.Notifications.Infrastructure.Persistence;
using Certiflow.Persistence;
using Contracts = Certiflow.Contracts;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Notifications.Infrastructure.Messaging;

/// <summary>
/// Shared inbox handling. Note what it does <b>not</b> do: deduplicate the notification.
/// <para>
/// The inbox stops the same <i>message</i> being processed twice. FR-7.5 is a different and stronger
/// requirement — one reminder per document per window, ever — and it holds across fifty-three
/// distinct nightly events, none of which the inbox would consider duplicates. That is the unique
/// index on the deduplication key, and this class must not be mistaken for it.
/// </para>
/// </summary>
public abstract class NotificationConsumerBase<TEvent>(NotificationsDbContext database, ISender sender)
    : IConsumer<TEvent>
    where TEvent : class, Contracts.IIntegrationEvent
{
    protected ISender Sender { get; } = sender;

    public async Task Consume(ConsumeContext<TEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var messageId = context.Message.EventId;

        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, context.CancellationToken))
        {
            return;
        }

        await HandleAsync(context.Message, context.CancellationToken);

        database.Inbox.Add(new InboxMessage(messageId, typeof(TEvent).Name, DateTimeOffset.UtcNow));
        await database.SaveChangesAsync(context.CancellationToken);
    }

    protected abstract Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}

/// <summary>Keeps the contact read model current (SRS §12).</summary>
public sealed class SupplierRegisteredNotificationConsumer(
    NotificationsDbContext database,
    ISender sender,
    ISupplierContactDirectory contacts)
    : NotificationConsumerBase<Contracts.SupplierRegistered>(database, sender)
{
    protected override async Task HandleAsync(Contracts.SupplierRegistered message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.PrimaryContactEmail))
        {
            // A supplier with no contact is registered but unreachable. Recording a blank address
            // would produce notifications that fail forever; skipping means they simply get none
            // until a contact exists.
            return;
        }

        await contacts.UpsertAsync(
            new SupplierContact(
                new SupplierId(message.SupplierId),
                message.LegalName,
                message.PrimaryContactEmail,
                message.PrimaryContactName ?? message.LegalName),
            cancellationToken);
    }
}

public sealed class DocumentApprovedNotificationConsumer(NotificationsDbContext database, ISender sender)
    : NotificationConsumerBase<Contracts.DocumentApproved>(database, sender)
{
    protected override Task HandleAsync(Contracts.DocumentApproved message, CancellationToken cancellationToken) =>
        Sender.Send(
            new RaiseNotificationCommand(
                DeduplicationKeys.Decision(new DocumentId(message.DocumentId), NotificationKind.DocumentApproved),
                message.SupplierId,
                NotificationKind.DocumentApproved,
                $"{message.DocumentType} approved",
                $"""
                 Your {message.DocumentType} certificate {message.CertificateNumber} has been approved.

                 Issued by: {message.IssuerName}
                 Valid until: {message.ExpiresOn:dd MMMM yyyy}

                 No further action is needed. We will remind you before it expires.
                 """),
            cancellationToken);
}

public sealed class DocumentRejectedNotificationConsumer(NotificationsDbContext database, ISender sender)
    : NotificationConsumerBase<Contracts.DocumentRejected>(database, sender)
{
    protected override Task HandleAsync(Contracts.DocumentRejected message, CancellationToken cancellationToken) =>
        Sender.Send(
            new RaiseNotificationCommand(
                DeduplicationKeys.Decision(new DocumentId(message.DocumentId), NotificationKind.DocumentRejected),
                message.SupplierId,
                NotificationKind.DocumentRejected,
                "A document you submitted was rejected",
                $"""
                 The document you submitted has been rejected.

                 Reason: {Readable(message.ReasonCode)}

                 Please upload a corrected document. The requirement remains outstanding until you do.
                 """),
            cancellationToken);

    /// <summary>
    /// A rejection is the one notification a supplier has to act on, so it should not arrive reading
    /// like an enum. This is where FR-7.6's templating would go when it lands.
    /// </summary>
    private static string Readable(string reasonCode) => reasonCode switch
    {
        "Illegible" => "the document could not be read clearly",
        "Expired" => "the certificate has already expired",
        "WrongDocumentType" => "it is not the type of document required",
        "HolderMismatch" => "the certificate is issued to a different organisation",
        "Suspected" => "the document could not be verified",
        _ => reasonCode,
    };
}

/// <summary>
/// The renewal reminders (FR-7.2), and the reason <see cref="ReminderWindow"/> exists.
/// </summary>
public sealed class CertificateExpiringSoonConsumer(NotificationsDbContext database, ISender sender)
    : NotificationConsumerBase<Contracts.CertificateExpiringSoon>(database, sender)
{
    protected override async Task HandleAsync(
        Contracts.CertificateExpiringSoon message,
        CancellationToken cancellationToken)
    {
        // The sweep raises this every night for as long as the certificate sits inside its renewal
        // window. Mapping the day count to a window, and keying on that, is what turns fifty-three
        // events into three emails.
        if (ReminderWindow.For(message.DaysRemaining) is not { } window)
        {
            return;
        }

        await Sender.Send(
            new RaiseNotificationCommand(
                DeduplicationKeys.Reminder(new DocumentId(message.DocumentId), window),
                message.SupplierId,
                NotificationKind.CertificateExpiringSoon,
                $"{message.DocumentType} expires in {message.DaysRemaining} days",
                $"""
                 Your {message.DocumentType} certificate expires on {message.ExpiresOn:dd MMMM yyyy}.

                 That is {message.DaysRemaining} days from now. Please upload a renewal before then to
                 stay compliant.
                 """),
            cancellationToken);
    }
}

public sealed class CertificateExpiredConsumer(NotificationsDbContext database, ISender sender)
    : NotificationConsumerBase<Contracts.CertificateExpired>(database, sender)
{
    protected override Task HandleAsync(Contracts.CertificateExpired message, CancellationToken cancellationToken) =>
        Sender.Send(
            new RaiseNotificationCommand(
                DeduplicationKeys.Decision(new DocumentId(message.DocumentId), NotificationKind.CertificateExpired),
                message.SupplierId,
                NotificationKind.CertificateExpired,
                $"{message.DocumentType} has expired",
                $"""
                 Your {message.DocumentType} certificate expired on {message.ExpiredOn:dd MMMM yyyy}.

                 You are no longer compliant with this requirement. Please upload a valid certificate.
                 """),
            cancellationToken);
}

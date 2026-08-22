using Certiflow.Notifications.Application.Abstractions;
using Certiflow.Notifications.Domain;
using Certiflow.SharedKernel;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Certiflow.Notifications.Application.Delivery;

/// <summary>
/// Raises one notification, unless an identical one already exists.
/// </summary>
public sealed record RaiseNotificationCommand(
    string DeduplicationKey,
    Guid SupplierId,
    NotificationKind Kind,
    string Subject,
    string Body) : IRequest<bool>;

public sealed partial class RaiseNotificationHandler(
    INotificationRepository repository,
    ISupplierContactDirectory contacts,
    INotificationSender sender,
    IClock clock,
    ILogger<RaiseNotificationHandler> logger)
    : IRequestHandler<RaiseNotificationCommand, bool>
{
    public async Task<bool> Handle(RaiseNotificationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var supplierId = new SupplierId(command.SupplierId);
        var contact = await contacts.FindAsync(supplierId, cancellationToken);

        if (contact is null)
        {
            // Dropped, not retried. The supplier's details arrive from BC1 on their own event, and a
            // notification about a supplier this service has never heard of is one nobody can act
            // on. Retrying forever would dead-letter it eventually and say nothing useful.
            NoContact(logger, command.SupplierId, command.Kind.ToString());
            return false;
        }

        var notification = Notification.Raise(
            command.DeduplicationKey,
            supplierId,
            contact.Email,
            command.Kind,
            command.Subject,
            command.Body,
            // Always recorded as the channel it *would* use. Whether it actually leaves is the
            // sender's decision (FR-7.8), and the record should not pretend the choice was never made.
            DeliveryChannel.Email,
            clock.UtcNow);

        repository.Add(notification);

        if (!await repository.SaveIfNewAsync(cancellationToken))
        {
            // The expected path for the fifty-second nightly sweep inside a renewal window.
            Deduplicated(logger, command.DeduplicationKey);
            return false;
        }

        var outcome = await sender.SendAsync(notification, cancellationToken);

        switch (outcome)
        {
            case DeliveryOutcome.Delivered:
                notification.MarkDelivered(clock.UtcNow);
                break;

            case DeliveryOutcome.Suppressed:
                notification.MarkSuppressed(clock.UtcNow);
                break;

            default:
                notification.MarkFailed("The channel refused the message.", clock.UtcNow);
                break;
        }

        // Saved again rather than in one transaction with the send, deliberately. The row must exist
        // before the send so a crash mid-send cannot produce a second one; the status is a fact
        // learned afterwards.
        await repository.SaveIfNewAsync(cancellationToken);

        return true;
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning,
        Message = "No contact for supplier {SupplierId}; dropping {Kind} notification")]
    private static partial void NoContact(ILogger logger, Guid supplierId, string kind);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Debug,
        Message = "Notification {Key} already exists; nothing sent")]
    private static partial void Deduplicated(ILogger logger, string key);
}

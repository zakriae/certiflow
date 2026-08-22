using Certiflow.Notifications.Application.Abstractions;
using Certiflow.Notifications.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Certiflow.Notifications.Infrastructure.Delivery;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// <b>False, and it takes a deliberate act to change that.</b>
    /// <para>
    /// FR-7.8 is not a preference: a publicly reachable demo that can send real mail to any address
    /// anyone types is an open relay with extra steps. Every message is recorded and shown in the
    /// in-app inbox instead, which is what a reviewer of this project actually wants to see anyway.
    /// </para>
    /// </summary>
    public bool OutboundEmailEnabled { get; set; }

    public string FromAddress { get; set; } = "no-reply@certiflow.demo";
}

/// <summary>
/// The only sender wired up. It records the message and, unless outbound mail has been explicitly
/// enabled, reports it as suppressed rather than delivered.
/// </summary>
public sealed partial class InAppNotificationSender(
    IOptions<NotificationOptions> options,
    ILogger<InAppNotificationSender> logger) : INotificationSender
{
    private readonly NotificationOptions _options = options.Value;

    public Task<DeliveryOutcome> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!_options.OutboundEmailEnabled)
        {
            // Logged at Information with the recipient, because "who would have been mailed" is the
            // question anyone demonstrating this will ask, and the answer should not require a
            // database query.
            Suppressed(logger, notification.Recipient, notification.Subject);

            return Task.FromResult(DeliveryOutcome.Suppressed);
        }

        // Deliberately not implemented. Wiring an SMTP client that nothing is allowed to call would
        // be dead code pretending to be a feature; the shape that matters - a channel, an outcome,
        // and a switch that defaults to off - is here, and an implementation slots in behind it.
        NoTransport(logger, notification.Recipient);

        return Task.FromResult(DeliveryOutcome.Failed);
    }

    [LoggerMessage(EventId = 7010, Level = LogLevel.Information,
        Message = "Held (outbound email disabled): to {Recipient} — {Subject}")]
    private static partial void Suppressed(ILogger logger, string recipient, string subject);

    [LoggerMessage(EventId = 7011, Level = LogLevel.Error,
        Message = "Outbound email is enabled but no transport is configured; message to {Recipient} was not sent")]
    private static partial void NoTransport(ILogger logger, string recipient);
}

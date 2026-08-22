using Certiflow.SharedKernel;

namespace Certiflow.Notifications.Domain;

/// <summary>
/// One message to one recipient.
/// <para>
/// The aggregate exists mainly to own <see cref="DeduplicationKey"/>. Everything else here is a
/// record of what was said and whether it left the building.
/// </para>
/// </summary>
public sealed class Notification : AggregateRoot<NotificationId>
{
    private Notification(
        NotificationId id,
        string deduplicationKey,
        SupplierId supplierId,
        string recipient,
        NotificationKind kind,
        string subject,
        string body,
        DeliveryChannel channel,
        DateTimeOffset raisedAt)
    {
        Id = id;
        DeduplicationKey = deduplicationKey;
        SupplierId = supplierId;
        Recipient = recipient;
        Kind = kind;
        Subject = subject;
        Body = body;
        Channel = channel;
        RaisedAt = raisedAt;
        Status = DeliveryStatus.Pending;
    }

    /// <summary>
    /// What makes "one reminder per document per window, ever" enforceable (FR-7.5).
    /// <para>
    /// A unique index on this column is the actual guarantee. Checking for an existing row before
    /// inserting is not: two deliveries of the same event, or two replicas processing it at once,
    /// both check, both find nothing, and both insert. The database has to be the one saying no.
    /// </para>
    /// </summary>
    public string DeduplicationKey { get; private set; }

    public SupplierId SupplierId { get; private set; }

    public string Recipient { get; private set; }

    public NotificationKind Kind { get; private set; }

    public string Subject { get; private set; }

    public string Body { get; private set; }

    public DeliveryChannel Channel { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public string? FailureReason { get; private set; }

    public static Notification Raise(
        string deduplicationKey,
        SupplierId supplierId,
        string recipient,
        NotificationKind kind,
        string subject,
        string body,
        DeliveryChannel channel,
        DateTimeOffset now)
    {
        Guard.AgainstNullOrWhiteSpace(deduplicationKey, "notification.deduplication_key_required");
        Guard.AgainstNullOrWhiteSpace(recipient, "notification.recipient_required");
        Guard.AgainstNullOrWhiteSpace(subject, "notification.subject_required");

        return new Notification(
            NotificationId.New(), deduplicationKey.Trim(), supplierId, recipient.Trim(),
            kind, subject.Trim(), body ?? string.Empty, channel, now);
    }

    public void MarkDelivered(DateTimeOffset now)
    {
        Status = DeliveryStatus.Delivered;
        DeliveredAt = now;
        FailureReason = null;
    }

    /// <summary>
    /// Recorded as held, not delivered, and not failed either — nothing went wrong. The distinction
    /// lets the demo inbox show exactly what would have been emailed without claiming it was sent.
    /// </summary>
    public void MarkSuppressed(DateTimeOffset now)
    {
        Status = DeliveryStatus.Suppressed;
        DeliveredAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        Guard.AgainstNullOrWhiteSpace(reason, "notification.failure_reason_required");

        Status = DeliveryStatus.Failed;
        FailureReason = reason;
        DeliveredAt = now;
    }

    /// <summary>Idempotent: reading something twice does not make it newly read.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

/// <summary>
/// Builds the deduplication keys, in one place so the two halves of a key cannot drift apart.
/// </summary>
public static class DeduplicationKeys
{
    /// <summary>
    /// One renewal reminder per document per window, ever (FR-7.5). The document id is what makes a
    /// genuine renewal notifiable again: a replacement certificate is a different document, so it
    /// gets its own three reminders.
    /// </summary>
    public static string Reminder(DocumentId documentId, ReminderWindow window) =>
        $"reminder:{documentId.Value}:{window}";

    /// <summary>
    /// One per decision per document. A redelivered <c>DocumentApproved</c> must not email a supplier
    /// twice about the same approval.
    /// </summary>
    public static string Decision(DocumentId documentId, NotificationKind kind) =>
        $"decision:{documentId.Value}:{kind}";

    /// <summary>
    /// Per supplier, requirement and day. A nightly sweep that finds the same requirement missing
    /// should say so once a day at most, not once per sweep.
    /// </summary>
    public static string Missing(SupplierId supplierId, Guid requirementId, DateOnly on) =>
        $"missing:{supplierId.Value}:{requirementId}:{on:O}";

    /// <summary>Per supplier and status transition day, so a flapping supplier does not spam admins.</summary>
    public static string StatusChange(SupplierId supplierId, string status, DateOnly on) =>
        $"status:{supplierId.Value}:{status}:{on:O}";
}

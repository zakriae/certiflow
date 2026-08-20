namespace Certiflow.Persistence;

/// <summary>
/// An integration event waiting to be published.
/// <para>
/// <b>The row that makes event-driven correct rather than best-effort.</b> It is written in the
/// same transaction as the state change that produced it, so the two cannot disagree: there is no
/// window where a document is stored but <c>DocumentStored</c> was never published, and none where
/// the event went out for a write that rolled back. A background dispatcher then publishes and
/// marks it (SRS §5.3, §19 Q6).
/// </para>
/// <para>
/// Publishing is at-least-once by construction — a crash between "published to the broker" and
/// "marked as published" replays the message. That is why every consumer must be idempotent on
/// <see cref="EventId"/>, and why this design pushes the hard problem to exactly one place.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        EventType = null!;
        PayloadJson = null!;
    }

    public OutboxMessage(Guid eventId, Guid correlationId, string eventType, string payloadJson, DateTimeOffset occurredAt)
    {
        EventId = eventId;
        CorrelationId = correlationId;
        EventType = eventType;
        PayloadJson = payloadJson;
        OccurredAt = occurredAt;
    }

    /// <summary>UUIDv7 so the table clusters in insertion order rather than fragmenting.</summary>
    public Guid EventId { get; private set; }

    public Guid CorrelationId { get; private set; }

    /// <summary>Assembly-qualified enough to resolve the contract type on the publishing side.</summary>
    public string EventType { get; private set; }

    public string PayloadJson { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>
    /// Counted so a message that keeps failing to publish becomes visible rather than retrying
    /// silently forever.
    /// </summary>
    public int PublishAttempts { get; private set; }

    public string? LastError { get; private set; }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        PublishedAt = publishedAt;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        PublishAttempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
    }
}

namespace Certiflow.Persistence;

/// <summary>
/// A message this service has already handled.
/// <para>
/// <b>The other half of at-least-once delivery.</b> The outbox guarantees an event is published
/// even if the publisher crashes — which means it can be published <em>twice</em>. This table is
/// how a consumer stays correct when that happens: the message id is inserted in the same
/// transaction as the work it authorised, so a replay finds the row and does nothing
/// (SRS §5.3, §19 Q6).
/// </para>
/// <para>
/// The deduplication <em>is</em> the primary key. Relying on a prior read having seen the row would
/// leave a race between two consumers handling the same redelivery at once; a duplicate insert
/// cannot race.
/// </para>
/// <para>
/// It is not a lock, a cache or a queue — it is a record that this exact message has been dealt
/// with, keyed on the publisher's own event id.
/// </para>
/// </summary>
public sealed class InboxMessage
{
    private InboxMessage() => MessageType = null!;

    public InboxMessage(Guid messageId, string messageType, DateTimeOffset receivedAt)
    {
        MessageId = messageId;
        MessageType = messageType;
        ReceivedAt = receivedAt;
    }

    public Guid MessageId { get; private set; }

    public string MessageType { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
}

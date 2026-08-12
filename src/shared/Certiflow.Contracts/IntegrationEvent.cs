namespace Certiflow.Contracts;

/// <summary>
/// The envelope every integration event carries (SRS §12).
/// <para>
/// <see cref="EventId"/> is the idempotency key: consumers record it and a redelivery becomes a
/// no-op. Service Bus is at-least-once, so this is not optional (SRS §19 Q6).
/// </para>
/// <para>
/// <see cref="CorrelationId"/> follows one user action across every service and into the audit
/// trail; <see cref="CausationId"/> is the <see cref="EventId"/> of the event that caused this
/// one, which is what makes an event chain replayable rather than merely logged.
/// </para>
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }

    Guid CorrelationId { get; }

    Guid? CausationId { get; }

    /// <summary>
    /// Additive-only within a major version. A breaking change publishes a new event type
    /// alongside the old one for one release rather than bumping this (SRS §12).
    /// </summary>
    int SchemaVersion { get; }
}

/// <summary>
/// Base record for integration events. Deriving records supply the payload; the envelope is
/// filled in here so no publisher can forget a correlation id.
/// </summary>
public abstract record IntegrationEvent : IIntegrationEvent
{
    protected IntegrationEvent(Guid correlationId, Guid? causationId = null)
    {
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    /// <summary>UUIDv7 so audit entries and outbox rows sort in insertion order.</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    public virtual int SchemaVersion => 1;
}

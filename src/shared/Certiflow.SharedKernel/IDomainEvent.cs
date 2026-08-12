namespace Certiflow.SharedKernel;

/// <summary>
/// Something that happened inside one aggregate, expressed in that context's own language.
/// A domain event never leaves its bounded context — it is translated into an integration
/// event from <c>Certiflow.Contracts</c> at the Infrastructure boundary (SRS §3, §12).
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Base record for domain events. <c>record</c> gives structural equality, which makes
/// asserting on raised events in domain tests a one-liner.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}

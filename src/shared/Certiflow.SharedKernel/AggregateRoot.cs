namespace Certiflow.SharedKernel;

/// <summary>
/// The consistency boundary. All state changes for the aggregate go through the root, so every
/// invariant in the SRS has exactly one place it can be enforced. Events are collected here and
/// drained by the Infrastructure layer inside the same transaction as the state change — that
/// is what makes the transactional outbox correct (SRS §5.3, §19 Q6).
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>
    /// Called by the unit of work once the events have been written to the outbox. Draining is
    /// the caller's job, not the aggregate's — an aggregate that clears its own events can lose
    /// them when a save fails.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}

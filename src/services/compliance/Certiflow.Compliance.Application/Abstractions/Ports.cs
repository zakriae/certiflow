using Certiflow.Compliance.Domain;

namespace Certiflow.Compliance.Application.Abstractions;

/// <summary>
/// Loads and stores <see cref="SupplierComplianceState"/> aggregates.
/// <para>
/// Defined here, implemented in Infrastructure — the dependency inversion that lets every handler
/// below be tested against a substitute rather than a database.
/// </para>
/// </summary>
public interface ISupplierComplianceRepository
{
    Task<SupplierComplianceState?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken);

    Task AddAsync(SupplierComplianceState state, CancellationToken cancellationToken);

    /// <summary>
    /// The ids of every supplier in a category, for rebuilding obligations when a profile version
    /// is published. Ids rather than aggregates: a category can hold thousands of suppliers, and
    /// they are processed one at a time.
    /// </summary>
    Task<IReadOnlyList<SupplierId>> ListSupplierIdsInCategoryAsync(Guid categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Every supplier id, for the nightly Expiry Watch (FR-5.4).
    /// <para>
    /// Deliberately ids and not aggregates. Loading every supplier's full state into memory to run
    /// a sweep is the kind of thing that works fine on twelve seeded suppliers and falls over on a
    /// real portfolio; and processing one at a time means one supplier failing does not abandon the
    /// rest of the run.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SupplierId>> ListAllSupplierIdsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Commits the current unit of work.
/// <para>
/// Saving is what drains the aggregate's domain events into the outbox in the same transaction as
/// the state change — which is what makes the event-driven side correct rather than best-effort
/// (SRS §5.3, §19 Q6). Handlers call this; they never publish anything themselves.
/// </para>
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

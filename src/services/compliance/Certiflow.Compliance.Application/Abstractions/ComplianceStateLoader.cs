using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Application.Abstractions;

/// <summary>
/// Loads a supplier's compliance state, bringing it up to the latest published profile first.
/// <para>
/// <b>Why this exists — a race found by running the system, not by reading it.</b>
/// <c>SupplierRegistered</c> and <c>ComplianceProfileVersionPublished</c> are separate messages on
/// separate queues, consumed concurrently. Registration applies the stored profile snapshot if one
/// exists; publication applies the new profile to every supplier it can see. Either order works on
/// its own — but if they interleave, registration finds no snapshot yet <em>and</em> publication's
/// supplier listing does not yet see the new row. The supplier ends up with zero obligations and
/// reads as vacuously Pending forever, with nothing in any log to say why.
/// </para>
/// <para>
/// Ordering cannot be assumed and adding a distributed lock would be a large hammer for a small
/// nail. Instead the state reconciles itself on the next read: if its profile version is behind the
/// stored snapshot, the snapshot is applied before anything else happens. Self-healing beats
/// correctly-ordered, because only one of the two survives a message being replayed a week later.
/// </para>
/// </summary>
public sealed class ComplianceStateLoader(
    ISupplierComplianceRepository repository,
    IComplianceProfileStore profileStore,
    IClock clock)
{
    public async Task<SupplierComplianceState> LoadAsync(SupplierId supplierId, CancellationToken cancellationToken)
    {
        var state = await repository.FindAsync(supplierId, cancellationToken)
            ?? throw new SupplierComplianceStateNotFoundException(supplierId);

        await ReconcileProfileAsync(state, cancellationToken);

        return state;
    }

    /// <summary>
    /// Applies the stored profile snapshot when the state is behind it. The aggregate ignores a
    /// version it already has, so this is free when nothing is stale.
    /// </summary>
    public async Task ReconcileProfileAsync(SupplierComplianceState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var snapshot = await profileStore.FindLatestAsync(state.CategoryId, cancellationToken);

        if (snapshot is null || snapshot.ProfileVersion <= state.ProfileVersion)
        {
            return;
        }

        state.ApplyProfileVersion(
            snapshot.ProfileVersion,
            [.. snapshot.Requirements.Select(requirement => requirement.ToSpecification())],
            clock.Today,
            clock.UtcNow);
    }
}

using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;
using NSubstitute;

namespace Certiflow.Compliance.Application.Tests;

/// <summary>
/// A frozen clock. The domain takes dates as parameters, so this only exists to let a handler be
/// driven to a chosen "today" without waiting for one.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// An in-memory repository. Substitutes are used for the interaction assertions, but the store
/// itself is real: a handler that loads, mutates and re-reads an aggregate is far better tested
/// against something that actually remembers than against a mock returning canned objects.
/// </summary>
internal sealed class InMemoryComplianceRepository : ISupplierComplianceRepository
{
    private readonly Dictionary<SupplierId, SupplierComplianceState> _states = [];

    public int SaveCount { get; private set; }

    public IReadOnlyCollection<SupplierComplianceState> All => _states.Values;

    public Task<SupplierComplianceState?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken) =>
        Task.FromResult(_states.GetValueOrDefault(supplierId));

    public Task AddAsync(SupplierComplianceState state, CancellationToken cancellationToken)
    {
        _states[state.Id] = state;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SupplierId>> ListSupplierIdsInCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierId>>(
            [.. _states.Values.Where(s => s.CategoryId == categoryId).Select(s => s.Id)]);

    public Task<IReadOnlyList<SupplierId>> ListAllSupplierIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierId>>([.. _states.Keys]);

    public void Seed(SupplierComplianceState state) => _states[state.Id] = state;
}

internal sealed class InMemoryProfileStore : IComplianceProfileStore
{
    private readonly Dictionary<Guid, ProfileVersionSnapshot> _snapshots = [];

    public Task<ProfileVersionSnapshot?> FindLatestAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.GetValueOrDefault(categoryId));

    public Task SaveAsync(ProfileVersionSnapshot snapshot, CancellationToken cancellationToken)
    {
        _snapshots[snapshot.CategoryId] = snapshot;
        return Task.CompletedTask;
    }
}

internal sealed class CountingUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.FromResult(0);
    }
}

/// <summary>Shared identifiers and builders, so the tests read as behaviour rather than setup.</summary>
internal static class Fixture
{
    public static readonly DateTimeOffset Now = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 8, 18);

    public static readonly Guid Logistics = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid SupplierGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public static readonly Guid RequirementGuid = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static readonly Guid DocumentGuid = Guid.Parse("00000000-0000-0000-0000-000000000101");

    public static SupplierId Supplier => new(SupplierGuid);

    public static RequirementId Requirement => new(RequirementGuid);

    public static RequirementDefinition Iso9001(int leadTimeDays = 30, int minValidityDays = 0) =>
        new(RequirementGuid, "ISO 9001", IsMandatory: true, leadTimeDays, minValidityDays);

    /// <summary>A registered supplier with one published mandatory requirement and no evidence.</summary>
    public static SupplierComplianceState RegisteredWithProfile()
    {
        var state = SupplierComplianceState.Register(Supplier, Logistics);
        state.ApplyProfileVersion(1, [Iso9001().ToSpecification()], Today, Now);
        state.ClearDomainEvents();
        return state;
    }

    public static IUnitOfWork SilentUnitOfWork() => Substitute.For<IUnitOfWork>();

    /// <summary>
    /// The loader the handlers take. Real, not a substitute: reconciling a stale profile on load is
    /// behaviour these tests depend on, so stubbing it out would test the wrong thing.
    /// </summary>
    public static ComplianceStateLoader Loader(
        InMemoryComplianceRepository repository,
        InMemoryProfileStore profiles,
        FixedClock clock) => new(repository, profiles, clock);
}

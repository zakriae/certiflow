using System.Text.Json;
using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Compliance.Infrastructure.Persistence;

public sealed class SupplierComplianceRepository(ComplianceDbContext context) : ISupplierComplianceRepository
{
    public async Task<SupplierComplianceState?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken) =>
        await context.SupplierCompliance.FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

    public async Task AddAsync(SupplierComplianceState state, CancellationToken cancellationToken) =>
        await context.SupplierCompliance.AddAsync(state, cancellationToken);

    public async Task<IReadOnlyList<SupplierId>> ListSupplierIdsInCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        await context.SupplierCompliance
            .Where(s => s.CategoryId == categoryId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SupplierId>> ListAllSupplierIdsAsync(CancellationToken cancellationToken) =>
        await context.SupplierCompliance.Select(s => s.Id).ToListAsync(cancellationToken);
}

/// <summary>
/// Stores the last published profile version per category so registration and publication can
/// arrive in either order (see <see cref="IComplianceProfileStore"/>).
/// </summary>
public sealed class ComplianceProfileStore(ComplianceDbContext context) : IComplianceProfileStore
{
    public async Task<ProfileVersionSnapshot?> FindLatestAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var record = await context.ProfileVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CategoryId == categoryId, cancellationToken);

        if (record is null)
        {
            return null;
        }

        var requirements = JsonSerializer.Deserialize<List<RequirementDefinition>>(
            record.RequirementsJson, ComplianceJson.Options) ?? [];

        return new ProfileVersionSnapshot(record.CategoryId, record.ProfileVersion, requirements);
    }

    public async Task SaveAsync(ProfileVersionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var json = JsonSerializer.Serialize(snapshot.Requirements, ComplianceJson.Options);
        var existing = await context.ProfileVersions.FirstOrDefaultAsync(
            p => p.CategoryId == snapshot.CategoryId, cancellationToken);

        if (existing is null)
        {
            await context.ProfileVersions.AddAsync(
                new ProfileVersionRecord(snapshot.CategoryId, snapshot.ProfileVersion, json), cancellationToken);

            return;
        }

        // Older versions are ignored: at-least-once delivery gives no ordering guarantee, and
        // rolling the rules backwards is worse than dropping a stale message.
        if (snapshot.ProfileVersion > existing.ProfileVersion)
        {
            existing.Update(snapshot.ProfileVersion, json);
        }
    }
}

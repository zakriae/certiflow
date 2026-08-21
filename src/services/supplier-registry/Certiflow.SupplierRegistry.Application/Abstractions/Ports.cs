using Certiflow.SupplierRegistry.Domain;

namespace Certiflow.SupplierRegistry.Application.Abstractions;

public interface ISupplierRepository
{
    Task<Supplier?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken);

    /// <summary>
    /// Registration numbers are unique per country (SRS §6.1), so the check needs both.
    /// </summary>
    Task<Supplier?> FindByRegistrationAsync(
        RegistrationNumber registrationNumber,
        CountryCode country,
        CancellationToken cancellationToken);

    Task AddAsync(Supplier supplier, CancellationToken cancellationToken);
}

public interface IComplianceProfileRepository
{
    Task<ComplianceProfile?> FindAsync(CategoryId categoryId, CancellationToken cancellationToken);

    Task AddAsync(ComplianceProfile profile, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

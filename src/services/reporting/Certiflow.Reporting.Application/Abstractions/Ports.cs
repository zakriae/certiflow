using Certiflow.Reporting.Domain;
using Certiflow.SharedKernel;

namespace Certiflow.Reporting.Application.Abstractions;

public interface IReportRepository
{
    Task<Report?> FindAsync(ReportId id, CancellationToken cancellationToken);

    void Add(Report report);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the facts a report asserts, at the moment it is generated.
/// <para>
/// <b>Read the implementation before assuming this is a read model.</b> It is not — it calls
/// Compliance and Supplier Registry over HTTP and fails the report if either cannot answer
/// (ADR-0006). A compliance certificate is a point-in-time attestation with a verification hash on
/// it; issuing one from an eventually-consistent local copy would mean signing for facts that were
/// true a few seconds ago, which is the one guarantee this document is supposed to provide.
/// </para>
/// </summary>
public interface IComplianceSnapshotSource
{
    Task<SupplierComplianceSnapshot> FetchAsync(SupplierId supplierId, CancellationToken cancellationToken);
}

/// <summary>
/// Renders a snapshot to PDF bytes. Kept behind a port so the generation handler — which owns the
/// ordering and hashing rules that make a fingerprint reproducible — is testable without QuestPDF.
/// </summary>
public interface IReportRenderer
{
    byte[] Render(SupplierComplianceSnapshot snapshot, ReportId reportId, string verificationHash, DateTimeOffset generatedAt);
}

public interface IReportBlobStore
{
    Task<StorageReference> StoreAsync(byte[] content, string blobPath, CancellationToken cancellationToken);

    Task<string> CreateReadUrlAsync(StorageReference reference, TimeSpan lifetime, CancellationToken cancellationToken);
}

/// <summary>Raised when the subject supplier does not exist or cannot be reached.</summary>
public sealed class SnapshotUnavailableException(string message) : Exception(message);

public sealed class ReportNotFoundException(ReportId id)
    : Exception($"Report {id} was not found."), IResourceNotFound;

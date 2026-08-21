using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.SharedKernel;

namespace Certiflow.Reporting.Application.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

internal sealed class InMemoryReportRepository : IReportRepository
{
    private readonly List<Report> _reports = [];

    public IReadOnlyList<Report> All => _reports;

    public Task<Report?> FindAsync(ReportId id, CancellationToken cancellationToken) =>
        Task.FromResult(_reports.SingleOrDefault(r => r.Id == id));

    public void Add(Report report) => _reports.Add(report);

    public void Seed(Report report) => _reports.Add(report);
}

internal sealed class CountingUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Captures what it was asked to render, so the handler's ordering can be asserted on.</summary>
internal sealed class CapturingRenderer : IReportRenderer
{
    public SupplierComplianceSnapshot? LastSnapshot { get; private set; }

    public string? LastVerificationHash { get; private set; }

    public byte[] Render(SupplierComplianceSnapshot snapshot, ReportId reportId, string verificationHash, DateTimeOffset generatedAt)
    {
        LastSnapshot = snapshot;
        LastVerificationHash = verificationHash;

        return [0x25, 0x50, 0x44, 0x46];
    }
}

internal sealed class InMemoryBlobStore : IReportBlobStore
{
    public List<string> Paths { get; } = [];

    public Task<StorageReference> StoreAsync(byte[] content, string blobPath, CancellationToken cancellationToken)
    {
        Paths.Add(blobPath);
        return Task.FromResult(StorageReference.Create("reports", blobPath));
    }

    public Task<string> CreateReadUrlAsync(StorageReference reference, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult($"https://example.invalid/{reference.BlobPath}");
}

internal static class Fixture
{
    public static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    public static readonly SupplierId Supplier = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    public static SupplierComplianceSnapshot Snapshot(params ObligationLine[] obligations) =>
        new(Supplier, "Meridian Logistics SARL", "Meridian", "FR-882-119", "FR", "Logistics", 1, "Compliant",
            new DateOnly(2026, 3, 14),
            obligations.Length == 0 ? [Obligation("ISO 9001", true)] : obligations);

    public static ObligationLine Obligation(string documentType, bool mandatory, string status = "Satisfied") =>
        new(new RequirementId(Guid.NewGuid()), documentType, mandatory, status, 400, null);
}

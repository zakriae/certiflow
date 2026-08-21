using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Application.Generation;
using Certiflow.Reporting.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

using static Certiflow.Reporting.Application.Tests.Fixture;

namespace Certiflow.Reporting.Application.Tests;

public sealed class GenerateReportHandlerTests
{
    private readonly InMemoryReportRepository _repository = new();

    private readonly CountingUnitOfWork _unitOfWork = new();

    private readonly CapturingRenderer _renderer = new();

    private readonly InMemoryBlobStore _blobs = new();

    private readonly FixedClock _clock = new(Now);

    private readonly IComplianceSnapshotSource _snapshots = Substitute.For<IComplianceSnapshotSource>();

    private GenerateReportHandler Handler => new(
        _repository, _snapshots, _renderer, _blobs, _unitOfWork, _clock,
        NullLogger<GenerateReportHandler>.Instance);

    private Report Seeded()
    {
        var report = Report.Request(ReportType.SupplierComplianceCertificate, Supplier, "buyer@certiflow.demo", Now);
        _repository.Seed(report);

        return report;
    }

    private void Returns(SupplierComplianceSnapshot snapshot) =>
        _snapshots.FetchAsync(Supplier, Arg.Any<CancellationToken>()).Returns(snapshot);

    [Fact]
    public async Task A_generated_report_carries_the_fingerprint_of_the_facts_it_shows()
    {
        var snapshot = Snapshot();
        Returns(snapshot);
        var report = Seeded();

        await Handler.Handle(new GenerateReportCommand(report.Id.Value), CancellationToken.None);

        report.Status.Should().Be(ReportStatus.Completed);
        report.VerificationHash.Should().Be(ReportFingerprint.Compute(snapshot));
        _renderer.LastVerificationHash.Should().Be(report.VerificationHash,
            "the PDF prints the hash of its own contents");
    }

    [Fact]
    public async Task Obligations_are_sorted_before_hashing_so_the_fingerprint_is_reproducible()
    {
        // Without this the hash depends on whatever order the compliance service happened to return,
        // and the same facts would verify on one run and fail on the next.
        var optional = Obligation("Trade Licence", mandatory: false);
        var mandatoryB = Obligation("ISO 9001", mandatory: true);
        var mandatoryA = Obligation("Food Hygiene Certificate", mandatory: true);

        Returns(Snapshot(optional, mandatoryB, mandatoryA));
        var report = Seeded();

        await Handler.Handle(new GenerateReportCommand(report.Id.Value), CancellationToken.None);

        _renderer.LastSnapshot!.Obligations.Select(o => o.DocumentType)
            .Should().Equal("Food Hygiene Certificate", "ISO 9001", "Trade Licence");
    }

    [Fact]
    public async Task The_same_facts_in_a_different_order_produce_the_same_fingerprint()
    {
        var a = Obligation("ISO 9001", true);
        var b = Obligation("Trade Licence", false);

        Returns(Snapshot(a, b));
        var first = Seeded();
        await Handler.Handle(new GenerateReportCommand(first.Id.Value), CancellationToken.None);

        Returns(Snapshot(b, a));
        var second = Seeded();
        await Handler.Handle(new GenerateReportCommand(second.Id.Value), CancellationToken.None);

        second.VerificationHash.Should().Be(first.VerificationHash);
    }

    [Fact]
    public async Task A_redelivered_job_does_not_render_a_second_time()
    {
        // FR-6.5: the artefact is immutable. Re-rendering would replace a report someone already
        // downloaded with a differently-dated one at the same id.
        Returns(Snapshot());
        var report = Seeded();
        await Handler.Handle(new GenerateReportCommand(report.Id.Value), CancellationToken.None);
        var storedPaths = _blobs.Paths.Count;

        await Handler.Handle(new GenerateReportCommand(report.Id.Value), CancellationToken.None);

        _blobs.Paths.Should().HaveCount(storedPaths);
        report.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task An_unreachable_compliance_service_fails_the_job_rather_than_dead_lettering_it()
    {
        // Letting this escape would leave the job stuck in Generating forever, and the caller unable
        // to tell a slow report from a dead one.
        _snapshots.FetchAsync(Supplier, Arg.Any<CancellationToken>())
            .Returns<SupplierComplianceSnapshot>(_ => throw new SnapshotUnavailableException("compliance unreachable"));

        var report = Seeded();

        var act = async () => await Handler.Handle(new GenerateReportCommand(report.Id.Value), CancellationToken.None);

        await act.Should().NotThrowAsync();
        report.Status.Should().Be(ReportStatus.Failed);
        report.FailureReason.Should().Be("compliance unreachable");
        report.VerificationHash.Should().BeNull("nothing was attested to");
    }

    [Fact]
    public async Task Two_reports_for_one_supplier_never_share_a_blob_path()
    {
        Returns(Snapshot());
        var first = Seeded();
        var second = Seeded();

        await Handler.Handle(new GenerateReportCommand(first.Id.Value), CancellationToken.None);
        await Handler.Handle(new GenerateReportCommand(second.Id.Value), CancellationToken.None);

        _blobs.Paths.Should().OnlyHaveUniqueItems("immutability has to hold in storage, not only in the aggregate");
    }

    [Fact]
    public async Task An_unknown_report_id_is_an_error_rather_than_a_silent_no_op()
    {
        var act = async () => await Handler.Handle(new GenerateReportCommand(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<ReportNotFoundException>();
    }
}

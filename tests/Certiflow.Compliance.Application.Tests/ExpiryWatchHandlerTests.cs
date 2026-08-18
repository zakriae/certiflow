using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Application.Evaluation;
using Certiflow.Compliance.Domain;
using Certiflow.Compliance.Domain.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

using static Certiflow.Compliance.Application.Tests.Fixture;

namespace Certiflow.Compliance.Application.Tests;

public sealed class ExpiryWatchHandlerTests
{
    private readonly InMemoryComplianceRepository _repository = new();

    private readonly CountingUnitOfWork _unitOfWork = new();

    private readonly FixedClock _clock = new(Now);

    private RunExpiryWatchHandler Handler =>
        new(_repository, _unitOfWork, _clock, NullLogger<RunExpiryWatchHandler>.Instance);

    /// <summary>A compliant supplier whose only certificate expires in <paramref name="days"/>.</summary>
    private static SupplierComplianceState CompliantUntil(int days, Guid? supplierId = null)
    {
        var id = new SupplierId(supplierId ?? SupplierGuid);
        var state = SupplierComplianceState.Register(id, Logistics);
        state.ApplyProfileVersion(1, [Iso9001(leadTimeDays: 30).ToSpecification()], Today, Now);

        state.ApplyApprovedEvidence(
            Requirement,
            new CertificateEvidence(
                new DocumentId(DocumentGuid),
                "FR-9001-00417",
                "AFNOR Certification",
                "Meridian Logistics SARL",
                new ValidityPeriod(Today.AddYears(-1), Today.AddDays(days)),
                "reviewer@certiflow.demo",
                Now),
            Today,
            Now);

        state.ClearDomainEvents();
        return state;
    }

    [Fact]
    public async Task A_certificate_lapsing_overnight_flips_the_supplier_non_compliant()
    {
        // Nothing about the data changed — only the date did. This is the whole point of FR-5.4.
        _repository.Seed(CompliantUntil(days: 1));
        _clock.Advance(TimeSpan.FromDays(2));

        var result = await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        result.SuppliersEvaluated.Should().Be(1);
        result.SuppliersFailed.Should().Be(0);

        var state = _repository.All.Single();
        state.OverallStatus.Should().Be(ComplianceStatus.NonCompliant);
        state.DomainEvents.OfType<CertificateExpired>().Should().ContainSingle();
        state.DomainEvents.OfType<SupplierBecameNonCompliant>().Should().ContainSingle();
    }

    [Fact]
    public async Task Crossing_into_the_renewal_window_raises_expiring_soon()
    {
        _repository.Seed(CompliantUntil(days: 40));
        _clock.Advance(TimeSpan.FromDays(11));

        await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        _repository.All.Single().DomainEvents.OfType<CertificateExpiringSoon>().Should().ContainSingle()
            .Which.DaysRemaining.Should().Be(29);
    }

    [Fact]
    public async Task A_quiet_night_produces_no_events_at_all()
    {
        // A sweep over every supplier that re-announces every state it finds is how a reminder
        // system trains its users to ignore it (FR-7.5).
        _repository.Seed(CompliantUntil(days: 400));

        await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        _repository.All.Single().DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Running_the_sweep_twice_on_the_same_day_is_idempotent()
    {
        // The timer trigger can fire twice for one night under at-least-once delivery.
        _repository.Seed(CompliantUntil(days: 1));
        _clock.Advance(TimeSpan.FromDays(2));

        await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);
        var afterFirst = _repository.All.Single().DomainEvents.Count;
        _repository.All.Single().ClearDomainEvents();

        await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        afterFirst.Should().BeGreaterThan(0);
        _repository.All.Single().DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task One_failing_supplier_does_not_abandon_the_rest_of_the_run()
    {
        // The property that decides whether a nightly job is trustworthy. An all-or-nothing sweep
        // is a sweep that eventually stops doing anything and nobody notices.
        var healthy = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var repository = Substitute.For<ISupplierComplianceRepository>();

        repository.ListAllSupplierIdsAsync(Arg.Any<CancellationToken>())
            .Returns([new SupplierId(SupplierGuid), new SupplierId(healthy)]);

        repository.FindAsync(new SupplierId(SupplierGuid), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("corrupt row"));

        repository.FindAsync(new SupplierId(healthy), Arg.Any<CancellationToken>())
            .Returns(CompliantUntil(days: 1, supplierId: healthy));

        _clock.Advance(TimeSpan.FromDays(2));

        var handler = new RunExpiryWatchHandler(
            repository, _unitOfWork, _clock, NullLogger<RunExpiryWatchHandler>.Instance);

        var result = await handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        result.SuppliersFailed.Should().Be(1);
        result.SuppliersEvaluated.Should().Be(1, "the healthy supplier was still evaluated");
    }

    [Fact]
    public async Task Each_supplier_is_saved_separately()
    {
        // Per-supplier commits are what make the isolation above real rather than theoretical.
        _repository.Seed(CompliantUntil(days: 1));
        _repository.Seed(CompliantUntil(days: 1, supplierId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003")));
        _clock.Advance(TimeSpan.FromDays(2));

        await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        _unitOfWork.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task The_result_reports_the_date_the_sweep_evaluated_against()
    {
        _repository.Seed(CompliantUntil(days: 400));
        _clock.Advance(TimeSpan.FromDays(3));

        var result = await Handler.Handle(new RunExpiryWatchCommand(), CancellationToken.None);

        result.EvaluatedOn.Should().Be(Today.AddDays(3));
    }
}

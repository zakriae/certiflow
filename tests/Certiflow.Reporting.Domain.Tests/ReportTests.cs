using Certiflow.Reporting.Domain;
using Certiflow.Reporting.Domain.Events;
using Events = Certiflow.Reporting.Domain.Events;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Reporting.Domain.Tests;

public sealed class ReportTests
{
    private static readonly SupplierId Supplier = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly StorageReference Blob =
        StorageReference.Create("reports", "2026/03/supplier-compliance.pdf");

    private static Report Requested() =>
        Report.Request(ReportType.SupplierComplianceCertificate, Supplier, "buyer@certiflow.demo", Now);

    [Fact]
    public void A_requested_report_is_not_yet_an_artefact()
    {
        var report = Requested();

        report.Status.Should().Be(ReportStatus.Requested);
        report.Storage.Should().BeNull();
        report.VerificationHash.Should().BeNull();
        report.DomainEvents.OfType<ReportCompleted>().Should().BeEmpty("nothing has been produced yet");
        report.DomainEvents.OfType<Events.ReportRequested>().Should().ContainSingle(
            "the request itself travels through the outbox, which is what makes the 202 durable");
    }

    [Fact]
    public void Completing_records_the_artefact_and_announces_it()
    {
        var report = Requested();
        report.Start();

        report.Complete(Blob, "abc123", Now.AddSeconds(4));

        report.Status.Should().Be(ReportStatus.Completed);
        report.Storage.Should().Be(Blob);
        report.VerificationHash.Should().Be("abc123");
        report.CompletedAt.Should().Be(Now.AddSeconds(4));
        report.DomainEvents.OfType<ReportCompleted>().Should().ContainSingle()
            .Which.VerificationHash.Should().Be("abc123");
    }

    [Fact]
    public void A_redelivered_request_cannot_restart_a_job_that_already_ran()
    {
        // Service Bus is at-least-once. Without this, a redelivery would regenerate the PDF and
        // overwrite a completed report with a second, differently-dated one - which is exactly the
        // immutability FR-6.5 exists to guarantee.
        var report = Requested();
        report.Start();
        report.Complete(Blob, "abc123", Now);

        var act = () => report.Start();

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("reporting.report.already_started");
    }

    [Fact]
    public void A_completed_report_can_never_be_marked_failed()
    {
        var report = Requested();
        report.Start();
        report.Complete(Blob, "abc123", Now);

        var act = () => report.Fail("renderer crashed", Now.AddMinutes(1));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("reporting.report.already_completed");
    }

    [Fact]
    public void Failure_is_recorded_rather_than_discarded()
    {
        // A caller who asked for a report and got silence cannot tell "still working" from
        // "gave up an hour ago".
        var report = Requested();
        report.Start();

        report.Fail("compliance service unreachable", Now.AddSeconds(30));

        report.Status.Should().Be(ReportStatus.Failed);
        report.FailureReason.Should().Be("compliance service unreachable");
        report.CompletedAt.Should().Be(Now.AddSeconds(30));
        report.DomainEvents.OfType<ReportCompleted>().Should().BeEmpty("a failed report has no artefact to announce");
    }

    [Fact]
    public void Every_request_gets_its_own_identity()
    {
        // FR-6.5: re-running produces a new report rather than replacing the old one, so a report
        // downloaded last March still says what it said in March.
        Requested().Id.Should().NotBe(Requested().Id);
    }

    [Fact]
    public void A_report_must_name_who_asked_for_it()
    {
        var act = () => Report.Request(ReportType.SupplierComplianceCertificate, Supplier, "  ", Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("reporting.report.requested_by_required");
    }
}

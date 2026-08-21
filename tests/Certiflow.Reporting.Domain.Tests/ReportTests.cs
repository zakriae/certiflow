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

public sealed class ReportStateMachineTests
{
    private static readonly SupplierId Supplier = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_report_cannot_be_completed_without_first_being_claimed()
    {
        // Start is what marks a job as taken. Completing straight from Requested would let a second
        // worker produce an artefact for a job another worker is already rendering.
        var report = Report.Request(ReportType.SupplierComplianceCertificate, Supplier, "buyer@certiflow.demo", Now);

        var act = () => report.Complete(StorageReference.Create("reports", "a.pdf"), "abc123", Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("reporting.report.not_in_progress");
    }

    [Fact]
    public void A_failed_report_is_terminal_and_is_retried_by_requesting_a_new_one()
    {
        var report = Report.Request(ReportType.SupplierComplianceCertificate, Supplier, "buyer@certiflow.demo", Now);
        report.Start();
        report.Fail("compliance unreachable", Now);

        var act = () => report.Start();

        // A failed job stays failed. Retrying means POSTing a new request, which is the same rule
        // FR-6.5 applies to successful ones: every run is its own report with its own id and its
        // own fingerprint. Reviving this row would give one id two different histories.
        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("reporting.report.already_started");
    }
}

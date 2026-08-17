using Certiflow.Compliance.Domain.Events;
using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Domain.Tests.ComplianceScenario;

namespace Certiflow.Compliance.Domain.Tests;

/// <summary>
/// FR-5.4 — the nightly re-evaluation. The interesting property is not that it detects expiry;
/// it is that it stays quiet when nothing changed, because a sweep over every supplier that
/// re-announces every state it finds turns into a nightly flood of email.
/// </summary>
public sealed class ExpiryWatchTests
{
    [Fact]
    public void Crossing_into_the_renewal_window_raises_expiring_soon_exactly_once()
    {
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(40)), Today, Now);
        state.FindObligation(Requirement(1))!.Status.Should().Be(ObligationStatus.Satisfied);
        state.ClearDomainEvents();

        // Day 11: 29 days left, inside the 30-day window.
        state.Evaluate(Today.AddDays(11), Now.AddDays(11));

        state.DomainEvents.OfType<CertificateExpiringSoon>().Should().ContainSingle()
            .Which.DaysRemaining.Should().Be(29);
        state.OverallStatus.Should().Be(ComplianceStatus.AtRisk);

        state.ClearDomainEvents();

        // Day 12: still At Risk. Nothing transitioned, so nothing is announced.
        state.Evaluate(Today.AddDays(12), Now.AddDays(12));

        state.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Crossing_the_expiry_date_raises_expired_and_flips_the_supplier_non_compliant()
    {
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(5)), Today, Now);
        state.ClearDomainEvents();

        state.Evaluate(Today.AddDays(6), Now.AddDays(6));

        state.DomainEvents.OfType<CertificateExpired>().Should().ContainSingle()
            .Which.ExpiredOn.Should().Be(Today.AddDays(5));
        state.DomainEvents.OfType<ComplianceStatusChanged>().Should().ContainSingle()
            .Which.NewStatus.Should().Be(ComplianceStatus.NonCompliant);
        state.DomainEvents.OfType<SupplierBecameNonCompliant>().Should().ContainSingle()
            .Which.BreachedRequirements.Should().Equal([Requirement(1)]);
    }

    [Fact]
    public void Re_evaluating_the_same_date_is_idempotent()
    {
        // At-least-once delivery means the Expiry Watch trigger can fire twice for one night.
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(5)), Today, Now);
        state.ClearDomainEvents();

        state.Evaluate(Today.AddDays(6), Now.AddDays(6));
        var firstRun = state.DomainEvents.Count;
        state.ClearDomainEvents();

        state.Evaluate(Today.AddDays(6), Now.AddDays(6));

        firstRun.Should().BeGreaterThan(0);
        state.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Evaluation_records_when_it_last_ran()
    {
        var state = WithProfile(Spec());
        var evaluatedAt = Now.AddDays(3);

        state.Evaluate(Today.AddDays(3), evaluatedAt);

        state.LastEvaluatedAt.Should().Be(evaluatedAt);
    }

    [Fact]
    public void An_optional_obligation_expiring_notifies_but_does_not_break_compliance()
    {
        var state = WithProfile(
            Spec(requirement: 1, mandatory: true, renewalLeadTimeDays: 30),
            Spec(requirement: 2, documentType: "Safety Training", mandatory: false, renewalLeadTimeDays: 30));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(400)), Today, Now);
        state.ApplyApprovedEvidence(Requirement(2), Evidence(2, expiresOn: Today.AddDays(5)), Today, Now);
        state.ClearDomainEvents();

        state.Evaluate(Today.AddDays(6), Now.AddDays(6));

        state.DomainEvents.OfType<CertificateExpired>().Should().ContainSingle(
            "the supplier still needs chasing about it");
        state.DomainEvents.OfType<ComplianceStatusChanged>().Should().BeEmpty();
        state.OverallStatus.Should().Be(ComplianceStatus.Compliant);
    }
}

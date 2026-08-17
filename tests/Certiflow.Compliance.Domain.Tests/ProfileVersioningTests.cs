using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Domain.Tests.ComplianceScenario;

namespace Certiflow.Compliance.Domain.Tests;

/// <summary>
/// FR-1.4 — publishing a new profile version must not retroactively invalidate evidence a
/// supplier already holds. The version guard also covers the out-of-order delivery that
/// at-least-once messaging makes inevitable.
/// </summary>
public sealed class ProfileVersioningTests
{
    [Fact]
    public void A_surviving_requirement_keeps_its_evidence_across_a_new_profile_version()
    {
        var state = WithProfile(Spec(requirement: 1, renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(200)), Today, Now);

        state.ApplyProfileVersion(
            2,
            [Spec(requirement: 1, renewalLeadTimeDays: 60)],
            Today,
            Now);

        var obligation = state.FindObligation(Requirement(1))!;
        obligation.CurrentEvidence!.DocumentId.Should().Be(Document(1));
        obligation.Specification.RenewalLeadTimeDays.Should().Be(60);
        state.OverallStatus.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void Tightening_a_threshold_can_legitimately_move_an_obligation_to_at_risk()
    {
        // The rules changed, not the evidence. This is a real status change, not drift.
        var state = WithProfile(Spec(requirement: 1, renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(100)), Today, Now);
        state.OverallStatus.Should().Be(ComplianceStatus.Compliant);

        state.ApplyProfileVersion(2, [Spec(requirement: 1, renewalLeadTimeDays: 180)], Today, Now);

        state.OverallStatus.Should().Be(ComplianceStatus.AtRisk);
    }

    [Fact]
    public void Adding_a_mandatory_requirement_makes_a_compliant_supplier_non_compliant()
    {
        var state = WithProfile(Spec(requirement: 1));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(400)), Today, Now);
        state.ClearDomainEvents();

        state.ApplyProfileVersion(
            2,
            [Spec(requirement: 1), Spec(requirement: 2, documentType: "Trade Licence")],
            Today,
            Now);

        state.OverallStatus.Should().Be(ComplianceStatus.NonCompliant);
        state.FindObligation(Requirement(2))!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void A_dropped_requirement_stops_counting_but_keeps_its_evidence_history()
    {
        var state = WithProfile(
            Spec(requirement: 1),
            Spec(requirement: 2, documentType: "Trade Licence"));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(400)), Today, Now);
        state.ApplyApprovedEvidence(Requirement(2), Evidence(2, expiresOn: Today.AddDays(400)), Today, Now);

        state.ApplyProfileVersion(2, [Spec(requirement: 1)], Today, Now);

        var dropped = state.FindObligation(Requirement(2))!;
        dropped.IsApplicable.Should().BeFalse();
        dropped.CurrentEvidence.Should().BeNull();
        dropped.History.Should().ContainSingle()
            .Which.Reason.Should().Be(EvidenceRetirementReason.RequirementNoLongerApplicable);
        state.OverallStatus.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void A_dropped_requirement_refuses_new_evidence()
    {
        var state = WithProfile(Spec(requirement: 1), Spec(requirement: 2, documentType: "Trade Licence"));
        state.ApplyProfileVersion(2, [Spec(requirement: 1)], Today, Now);

        var act = () => state.ApplyApprovedEvidence(Requirement(2), Evidence(2), Today, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("compliance.obligation.not_in_profile");
    }

    [Fact]
    public void An_out_of_order_older_profile_version_is_ignored()
    {
        // Service Bus gives no global ordering. Applying v1 after v2 would silently roll the
        // rules back, which is worse than dropping the message.
        var state = WithProfile(Spec(requirement: 1, renewalLeadTimeDays: 30));
        state.ApplyProfileVersion(5, [Spec(requirement: 1, renewalLeadTimeDays: 90)], Today, Now);

        state.ApplyProfileVersion(2, [Spec(requirement: 1, renewalLeadTimeDays: 10)], Today, Now);

        state.ProfileVersion.Should().Be(5);
        state.FindObligation(Requirement(1))!.Specification.RenewalLeadTimeDays.Should().Be(90);
    }

    [Fact]
    public void Profile_version_must_be_positive()
    {
        var state = SupplierComplianceState.Register(Supplier(), LogisticsCategory);

        var act = () => state.ApplyProfileVersion(0, [Spec()], Today, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("compliance.profile.version_must_be_positive");
    }
}

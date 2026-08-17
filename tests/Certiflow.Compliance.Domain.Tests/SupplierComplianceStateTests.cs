using Certiflow.Compliance.Domain.Events;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Domain.Tests.ComplianceScenario;

namespace Certiflow.Compliance.Domain.Tests;

public sealed class SupplierComplianceStateTests
{
    [Fact]
    public void A_supplier_with_no_published_profile_is_pending_not_compliant()
    {
        // The trap this guards against: an empty obligation list satisfying a "worst of" fold
        // vacuously, and a brand-new supplier appearing on the dashboard as green.
        var state = SupplierComplianceState.Register(Supplier(), LogisticsCategory);

        state.OverallStatus.Should().Be(ComplianceStatus.Pending);
        state.ProfileVersion.Should().Be(0);
    }

    [Fact]
    public void A_missing_mandatory_obligation_makes_the_supplier_non_compliant()
    {
        var state = WithProfile(Spec(mandatory: true));

        state.OverallStatus.Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public void A_missing_optional_obligation_never_makes_the_supplier_non_compliant()
    {
        // SRS §10.1, explicitly. Optional requirements are chased, not enforced.
        var state = WithProfile(
            Spec(requirement: 1, mandatory: true),
            Spec(requirement: 2, documentType: "Safety Training", mandatory: false));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(200)), Today, Now);

        state.OverallStatus.Should().Be(ComplianceStatus.Compliant);
        state.FindObligation(Requirement(2))!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void Overall_status_is_the_worst_mandatory_obligation()
    {
        var state = WithProfile(
            Spec(requirement: 1, renewalLeadTimeDays: 30),
            Spec(requirement: 2, documentType: "Public Liability Insurance", renewalLeadTimeDays: 30),
            Spec(requirement: 3, documentType: "Trade Licence", renewalLeadTimeDays: 30));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(200)), Today, Now);
        state.ApplyApprovedEvidence(Requirement(2), Evidence(2, expiresOn: Today.AddDays(15)), Today, Now);
        state.ApplyApprovedEvidence(Requirement(3), Evidence(3, expiresOn: Today.AddDays(300)), Today, Now);

        // One At Risk among two Satisfied — the worst one wins.
        state.OverallStatus.Should().Be(ComplianceStatus.AtRisk);
    }

    [Fact]
    public void Approving_evidence_raises_satisfied_and_a_status_change()
    {
        var state = WithProfile(Spec());

        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(200)), Today, Now);

        state.DomainEvents.Should().ContainSingle(e => e is ObligationSatisfied);
        state.DomainEvents.OfType<ComplianceStatusChanged>().Should().ContainSingle()
            .Which.Should().Match<ComplianceStatusChanged>(e =>
                e.PreviousStatus == ComplianceStatus.NonCompliant &&
                e.NewStatus == ComplianceStatus.Compliant);
    }

    [Fact]
    public void Superseding_evidence_moves_the_old_evidence_to_history_and_never_deletes_it()
    {
        var state = WithProfile(Spec());
        var original = Evidence(1, expiresOn: Today.AddDays(10));
        state.ApplyApprovedEvidence(Requirement(1), original, Today, Now);

        state.ApplyApprovedEvidence(Requirement(1), Evidence(2, expiresOn: Today.AddDays(400)), Today, Now);

        var obligation = state.FindObligation(Requirement(1))!;
        obligation.CurrentEvidence!.DocumentId.Should().Be(Document(2));
        obligation.History.Should().ContainSingle()
            .Which.Should().Match<RetiredEvidence>(h =>
                h.Evidence == original && h.Reason == EvidenceRetirementReason.Superseded);
    }

    [Fact]
    public void The_same_document_cannot_be_attached_twice()
    {
        var state = WithProfile(Spec());
        state.ApplyApprovedEvidence(Requirement(1), Evidence(1), Today, Now);

        var act = () => state.ApplyApprovedEvidence(Requirement(1), Evidence(1), Today, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("compliance.obligation.evidence_already_attached");
    }

    [Fact]
    public void Evidence_for_a_requirement_outside_the_profile_is_refused()
    {
        // Fails loudly so the message lands in the DLQ where it is visible (NFR-6), rather than
        // being swallowed as a no-op and leaving BC1 and BC5 quietly inconsistent.
        var state = WithProfile(Spec(requirement: 1));

        var act = () => state.ApplyApprovedEvidence(Requirement(9), Evidence(), Today, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("compliance.obligation.not_in_profile");
    }

    [Fact]
    public void A_rejected_submission_leaves_evidence_already_in_force_untouched()
    {
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(1, expiresOn: Today.AddDays(200)), Today, Now);
        state.RecordSubmission(Requirement(1), Document(2), Today, Now);
        state.ClearDomainEvents();

        state.ClearSubmission(Requirement(1), Today, Now);

        state.FindObligation(Requirement(1))!.Status.Should().Be(ObligationStatus.Satisfied);
        state.FindObligation(Requirement(1))!.PendingDocumentId.Should().BeNull();
        state.DomainEvents.Should().BeEmpty("nothing actually changed status");
    }
}

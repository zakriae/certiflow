using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Domain.Tests.ComplianceScenario;

namespace Certiflow.Compliance.Domain.Tests;

/// <summary>
/// The status-derivation table of SRS §10.1, one test per row. These are the assertions that make
/// "compliance status is derived, never stored" a fact rather than a claim.
/// </summary>
public sealed class ObligationStatusDerivationTests
{
    [Fact]
    public void An_obligation_with_no_submission_is_missing()
    {
        var state = WithProfile(Spec());

        state.FindObligation(Requirement(1))!.StatusOn(Today)
            .Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void An_obligation_with_a_submitted_but_unapproved_document_is_awaiting_review()
    {
        var state = WithProfile(Spec());

        state.RecordSubmission(Requirement(1), Document(1), Today, Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.AwaitingReview);
    }

    [Fact]
    public void Approved_evidence_with_plenty_of_validity_left_is_satisfied()
    {
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(200)), Today, Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.Satisfied);
    }

    [Fact]
    public void Evidence_inside_the_renewal_window_is_at_risk()
    {
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(21)), Today, Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.AtRisk);
    }

    [Fact]
    public void Evidence_expiring_exactly_on_the_lead_time_boundary_is_at_risk()
    {
        // SRS §10.1: "AtRisk when valid but DaysRemaining <= RenewalLeadTimeDays" — inclusive.
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(30)), Today, Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.AtRisk);
    }

    [Fact]
    public void Obligation_is_not_satisfied_by_evidence_expiring_within_minimum_validity()
    {
        // A certificate with 10 days left does not really satisfy a requirement that demands 30,
        // even though it is technically still valid today.
        var state = WithProfile(Spec(renewalLeadTimeDays: 1, minValidityDays: 30));

        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(10)), Today, Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.AtRisk);
    }

    [Fact]
    public void Evidence_past_its_expiry_date_is_expired()
    {
        var state = WithProfile(Spec());

        state.ApplyApprovedEvidence(
            Requirement(1),
            Evidence(issuedOn: Today.AddYears(-2), expiresOn: Today.AddDays(-1)),
            Today,
            Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.Expired);
    }

    [Fact]
    public void A_renewal_awaiting_review_does_not_downgrade_evidence_still_in_force()
    {
        // The supplier has uploaded next year's certificate while this year's is still valid.
        // Reading as AwaitingReview here would be wrong: they are compliant right now.
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(200)), Today, Now);

        state.RecordSubmission(Requirement(1), Document(2), Today, Now);

        state.FindObligation(Requirement(1))!.Status
            .Should().Be(ObligationStatus.Satisfied);
    }

    [Fact]
    public void Status_on_a_future_date_reflects_expiry_without_any_evaluation_having_run()
    {
        // This is the anti-drift proof. Nothing ran, no job fired, no row was updated — and the
        // derived status is still correct for a date after expiry (SRS §19 Q12).
        var state = WithProfile(Spec(renewalLeadTimeDays: 30));
        state.ApplyApprovedEvidence(Requirement(1), Evidence(expiresOn: Today.AddDays(10)), Today, Now);

        var obligation = state.FindObligation(Requirement(1))!;

        obligation.StatusOn(Today.AddDays(11)).Should().Be(ObligationStatus.Expired);
        state.OverallStatusOn(Today.AddDays(11)).Should().Be(ComplianceStatus.NonCompliant);
    }
}

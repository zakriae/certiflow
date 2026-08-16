using Certiflow.SharedKernel;
using Certiflow.Verification.Domain.Events;
using FluentAssertions;
using Xunit;

using static Certiflow.Verification.Domain.Tests.ReviewScenario;

namespace Certiflow.Verification.Domain.Tests;

public sealed class ReviewTaskTests
{
    [Fact]
    public void A_raised_task_is_open_with_nothing_resolved()
    {
        var task = Open();

        task.Status.Should().Be(ReviewTaskStatus.Open);
        task.FieldReviews.Should().HaveCount(6);
        task.UnresolvedMandatoryFields.Should().HaveCount(5);
        task.CanApprove.Should().BeFalse();
        task.DomainEvents.OfType<ReviewTaskRaised>().Should().ContainSingle();
    }

    [Fact]
    public void A_task_with_no_fields_is_refused()
    {
        var act = () => Open(suggestions: []);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.no_fields");
    }

    [Fact]
    public void Approval_is_blocked_until_every_mandatory_field_is_resolved()
    {
        // FR-4.5. Without this, the rushed path through the UI is "ignore the amber field, click
        // approve" — which is exactly the behaviour the review step exists to prevent.
        var task = Open();
        task.ResolveField("holderName", "Meridian Logistics SARL", Reviewer, Now);

        var act = () => task.Approve(Reviewer, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Should().Match<DomainRuleViolationException>(e =>
                e.Rule == "verification.task.mandatory_fields_unresolved" &&
                e.Message.Contains("expiresOn", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unresolved_optional_field_does_not_block_approval()
    {
        var task = Open().WithAllMandatoryFieldsResolved();

        task.FieldReviews.Single(f => f.FieldName == "scope").IsResolved.Should().BeFalse();
        task.CanApprove.Should().BeTrue();
    }

    [Fact]
    public void The_person_who_uploaded_a_document_cannot_approve_it()
    {
        // Segregation of duties (SRS §9.1, FR-4.7), enforced server-side. This is the control an
        // auditor asks about first, and a UI-only version of it is worth nothing.
        var task = Open(uploadedBy: Supplier).WithAllMandatoryFieldsResolved(reviewerId: Supplier);

        var act = () => task.Approve(Supplier, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.segregation_of_duties");
    }

    [Fact]
    public void Segregation_of_duties_cannot_be_defeated_by_changing_case()
    {
        // Identity providers are not consistent about the casing of a UPN, and a control defeated
        // by capitalising an email address is not a control.
        var task = Open(uploadedBy: "Contact@Meridian-Logistics.Demo").WithAllMandatoryFieldsResolved();

        var act = () => task.Approve("contact@meridian-logistics.demo", Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.segregation_of_duties");
    }

    [Fact]
    public void The_uploader_cannot_reject_their_own_document_either()
    {
        var task = Open(uploadedBy: Supplier);

        var act = () => task.Reject(Supplier, RejectionReason.Illegible, null, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.segregation_of_duties");
    }

    [Fact]
    public void Approving_carries_the_reviewers_accepted_values_not_the_extracted_ones()
    {
        // BC5 must never reach back to the extraction to find out what was approved.
        var task = Open();
        task.WithAllMandatoryFieldsResolved();
        task.ResolveField("expiresOn", "2027-03-31", Reviewer, Now, "Corrected from the scanned original.");

        task.Approve(Reviewer, Now);

        var approved = task.DomainEvents.OfType<DocumentApproved>().Should().ContainSingle().Subject;
        approved.AcceptedValues["expiresOn"].Should().Be("2027-03-31");
        approved.ApprovedBy.Should().Be(Reviewer);
        task.Status.Should().Be(ReviewTaskStatus.Completed);
    }

    [Fact]
    public void A_correction_records_both_values_and_raises_an_event()
    {
        // FR-4.4 and FR-8.5: "Reviewer X corrected expiresOn from A to B".
        var task = Open();

        task.ResolveField("expiresOn", "2027-03-31", Reviewer, Now, "Date was misread from the scan.");

        var field = task.FieldReviews.Single(f => f.FieldName == "expiresOn");
        field.SuggestedValue.Should().Be("2027-03-13", "the model's claim is never overwritten");
        field.AcceptedValue.Should().Be("2027-03-31");
        field.WasCorrected.Should().BeTrue();
        field.ReviewerNote.Should().Be("Date was misread from the scan.");

        var corrected = task.DomainEvents.OfType<FieldCorrected>().Should().ContainSingle().Subject;
        corrected.SuggestedValue.Should().Be("2027-03-13");
        corrected.AcceptedValue.Should().Be("2027-03-31");
        corrected.OriginalConfidence.Should().Be(0.62m);
    }

    [Fact]
    public void Accepting_a_suggestion_unchanged_is_not_recorded_as_a_correction()
    {
        var task = Open();

        task.ResolveField("expiresOn", "2027-03-13", Reviewer, Now);

        task.FieldReviews.Single(f => f.FieldName == "expiresOn").WasCorrected.Should().BeFalse();
        task.DomainEvents.OfType<FieldCorrected>().Should().BeEmpty();
    }

    [Fact]
    public void A_field_cannot_be_resolved_to_nothing()
    {
        var task = Open();

        var act = () => task.ResolveField("expiresOn", "   ", Reviewer, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.field.accepted_value_required");
    }

    [Fact]
    public void Resolving_a_field_the_task_does_not_have_is_refused()
    {
        var task = Open();

        var act = () => task.ResolveField("auditorSignature", "J. Dupont", Reviewer, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.unknown_field");
    }

    [Fact]
    public void A_verdict_is_write_once()
    {
        // A mistake is corrected by a new submission, never by editing history — which is precisely
        // what an auditor is checking for.
        var task = Open().WithAllMandatoryFieldsResolved();
        task.Approve(Reviewer, Now);

        var act = () => task.Reject(Reviewer, RejectionReason.Illegible, null, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.already_decided");
    }

    [Fact]
    public void Rejection_requires_a_reason_from_the_controlled_list()
    {
        var task = Open();

        task.Reject(Reviewer, RejectionReason.HolderMismatch, "Issued to Meridian Logistics Group.", Now);

        var rejected = task.DomainEvents.OfType<DocumentRejected>().Should().ContainSingle().Subject;
        rejected.Reason.Should().Be(RejectionReason.HolderMismatch);
        rejected.ReasonNote.Should().Be("Issued to Meridian Logistics Group.");
    }

    [Fact]
    public void Rejecting_with_other_demands_an_explanation()
    {
        // "Other" with no note is the same as no reason at all, and it is the reason a supplier
        // will phone about.
        var task = Open();

        var act = () => task.Reject(Reviewer, RejectionReason.Other, null, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.verdict.other_requires_note");
    }

    [Fact]
    public void Rejection_does_not_require_the_fields_to_be_filled_in_first()
    {
        // A reviewer rejects precisely because something is wrong. Making them complete the fields
        // of a document they are throwing out would be absurd.
        var task = Open();

        task.Reject(Reviewer, RejectionReason.Illegible, null, Now);

        task.Status.Should().Be(ReviewTaskStatus.Completed);
        task.Verdict!.Decision.Should().Be(VerdictDecision.Rejected);
    }

    [Fact]
    public void A_cancelled_task_can_never_receive_a_verdict()
    {
        // FR-4.9. Otherwise a reviewer could approve a document that has already been replaced, and
        // BC5 would record evidence from a stale file.
        var task = Open().WithAllMandatoryFieldsResolved();
        task.Cancel("Superseded by a newer submission.");

        var act = () => task.Approve(Reviewer, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.cancelled");
    }

    [Fact]
    public void Cancelling_twice_is_harmless()
    {
        // DocumentSuperseded may be delivered more than once (NFR-5).
        var task = Open();
        task.Cancel("Superseded.");
        task.ClearDomainEvents();

        task.Cancel("Superseded.");

        task.DomainEvents.Should().BeEmpty();
        task.Status.Should().Be(ReviewTaskStatus.Cancelled);
    }

    [Fact]
    public void A_decided_task_cannot_be_cancelled()
    {
        var task = Open().WithAllMandatoryFieldsResolved();
        task.Approve(Reviewer, Now);

        var act = () => task.Cancel("Superseded.");

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("verification.task.already_decided");
    }

    [Fact]
    public void Assigning_a_task_moves_it_to_in_progress()
    {
        var task = Open();

        task.AssignTo(Reviewer);

        task.AssignedTo.Should().Be(Reviewer);
        task.Status.Should().Be(ReviewTaskStatus.InProgress);
        task.DomainEvents.OfType<ReviewTaskAssigned>().Should().ContainSingle();
    }
}

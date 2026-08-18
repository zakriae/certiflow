using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Application.Evidence;
using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Application.Tests.Fixture;

namespace Certiflow.Compliance.Application.Tests;

public sealed class RecordApprovedEvidenceHandlerTests
{
    private readonly InMemoryComplianceRepository _repository = new();

    private readonly CountingUnitOfWork _unitOfWork = new();

    private readonly FixedClock _clock = new(Now);

    private RecordApprovedEvidenceHandler Handler => new(_repository, _unitOfWork, _clock);

    private static RecordApprovedEvidenceCommand Command(Guid? documentId = null, int expiresInDays = 400) =>
        new(
            SupplierGuid,
            RequirementGuid,
            documentId ?? DocumentGuid,
            CertificateNumber: "FR-9001-00417",
            Issuer: "AFNOR Certification",
            HolderName: "Meridian Logistics SARL",
            IssuedOn: Today.AddYears(-1),
            ExpiresOn: Today.AddDays(expiresInDays),
            ApprovedBy: "reviewer@certiflow.demo",
            ApprovedAt: Now);

    [Fact]
    public async Task Approved_evidence_makes_the_supplier_compliant()
    {
        _repository.Seed(RegisteredWithProfile());

        await Handler.Handle(Command(), CancellationToken.None);

        var state = _repository.All.Single();
        state.OverallStatus.Should().Be(ComplianceStatus.Compliant);
        state.FindObligation(Requirement)!.CurrentEvidence!.CertificateNumber.Should().Be("FR-9001-00417");
        _unitOfWork.SaveCount.Should().Be(1, "the outbox is drained by the same save as the state change");
    }

    [Fact]
    public async Task Redelivering_the_same_approval_is_a_no_op_rather_than_an_error()
    {
        // Service Bus is at-least-once. The aggregate refuses to attach the same document twice —
        // correctly — so without this check a redelivery would throw forever and dead-letter a
        // message that had already been handled perfectly well.
        _repository.Seed(RegisteredWithProfile());
        await Handler.Handle(Command(), CancellationToken.None);

        var act = async () => await Handler.Handle(Command(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.All.Single().FindObligation(Requirement)!.History.Should().BeEmpty(
            "the second delivery must not supersede the evidence with itself");
        _unitOfWork.SaveCount.Should().Be(1, "nothing changed, so nothing was saved");
    }

    [Fact]
    public async Task A_genuine_renewal_supersedes_rather_than_being_treated_as_a_redelivery()
    {
        _repository.Seed(RegisteredWithProfile());
        await Handler.Handle(Command(expiresInDays: 20), CancellationToken.None);

        var renewal = Guid.Parse("00000000-0000-0000-0000-000000000102");
        await Handler.Handle(Command(documentId: renewal, expiresInDays: 400), CancellationToken.None);

        var obligation = _repository.All.Single().FindObligation(Requirement)!;
        obligation.CurrentEvidence!.DocumentId.Should().Be(new DocumentId(renewal));
        obligation.History.Should().ContainSingle("the superseded certificate is kept, never deleted");
    }

    [Fact]
    public async Task An_approval_for_an_unknown_supplier_is_retried_rather_than_swallowed()
    {
        // SupplierRegistered has probably not been processed yet. Throwing puts the message back on
        // the queue; swallowing it would lose the evidence permanently.
        var act = async () => await Handler.Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<SupplierComplianceStateNotFoundException>();
        _unitOfWork.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task An_incoherent_validity_period_is_refused_by_the_domain()
    {
        // FluentValidation checks the shape of the request; that expiry must follow issue is a
        // domain rule, and it is ValidityPeriod that enforces it.
        _repository.Seed(RegisteredWithProfile());
        var backwards = Command() with { IssuedOn = Today, ExpiresOn = Today.AddDays(-1) };

        var act = async () => await Handler.Handle(backwards, CancellationToken.None);

        (await act.Should().ThrowAsync<DomainRuleViolationException>())
            .Which.Rule.Should().Be("compliance.validity.expires_after_issued");
    }
}

public sealed class SubmissionHandlerTests
{
    private readonly InMemoryComplianceRepository _repository = new();

    private readonly CountingUnitOfWork _unitOfWork = new();

    private readonly FixedClock _clock = new(Now);

    [Fact]
    public async Task A_submission_moves_the_obligation_to_awaiting_review()
    {
        _repository.Seed(RegisteredWithProfile());
        var handler = new RecordSubmissionHandler(_repository, _unitOfWork, _clock);

        await handler.Handle(new RecordSubmissionCommand(SupplierGuid, RequirementGuid, DocumentGuid), CancellationToken.None);

        _repository.All.Single().FindObligation(Requirement)!.Status
            .Should().Be(ObligationStatus.AwaitingReview);
    }

    [Fact]
    public async Task A_rejection_returns_the_obligation_to_missing()
    {
        _repository.Seed(RegisteredWithProfile());
        await new RecordSubmissionHandler(_repository, _unitOfWork, _clock)
            .Handle(new RecordSubmissionCommand(SupplierGuid, RequirementGuid, DocumentGuid), CancellationToken.None);

        await new ClearSubmissionHandler(_repository, _unitOfWork, _clock)
            .Handle(new ClearSubmissionCommand(SupplierGuid, RequirementGuid), CancellationToken.None);

        _repository.All.Single().FindObligation(Requirement)!.Status
            .Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public async Task A_submission_for_an_unknown_supplier_is_retried()
    {
        var handler = new RecordSubmissionHandler(_repository, _unitOfWork, _clock);

        var act = async () => await handler.Handle(
            new RecordSubmissionCommand(SupplierGuid, RequirementGuid, DocumentGuid), CancellationToken.None);

        await act.Should().ThrowAsync<SupplierComplianceStateNotFoundException>();
    }
}

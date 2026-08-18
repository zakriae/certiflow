using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;

namespace Certiflow.Compliance.Application.Evidence;

/// <summary>
/// Binds approved evidence to an obligation — the operation that actually makes a supplier
/// compliant. Raised from BC4's <c>DocumentApproved</c> (FR-5.1).
/// <para>
/// The field values come from the verdict, not from the extraction: they are the reviewer's
/// <em>accepted</em> values and may differ from what the model read (SRS §4.3).
/// </para>
/// </summary>
public sealed record RecordApprovedEvidenceCommand(
    Guid SupplierId,
    Guid RequirementId,
    Guid DocumentId,
    string CertificateNumber,
    string Issuer,
    string HolderName,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string ApprovedBy,
    DateTimeOffset ApprovedAt) : IRequest;

public sealed class RecordApprovedEvidenceValidator : AbstractValidator<RecordApprovedEvidenceCommand>
{
    public RecordApprovedEvidenceValidator()
    {
        RuleFor(c => c.SupplierId).NotEmpty();
        RuleFor(c => c.RequirementId).NotEmpty();
        RuleFor(c => c.DocumentId).NotEmpty();
        RuleFor(c => c.CertificateNumber).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Issuer).NotEmpty().MaximumLength(200);
        RuleFor(c => c.HolderName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.ApprovedBy).NotEmpty();

        // Shape only. That the period is coherent — expiry after issue, plausible span — is the
        // domain's business, enforced by ValidityPeriod. FluentValidation rejects a malformed
        // request; the aggregate rejects an illegal state (tech-stack doc §3).
        RuleFor(c => c.ExpiresOn).NotEqual(default(DateOnly));
        RuleFor(c => c.IssuedOn).NotEqual(default(DateOnly));
    }
}

public sealed class RecordApprovedEvidenceHandler(
    ISupplierComplianceRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<RecordApprovedEvidenceCommand>
{
    public async Task Handle(RecordApprovedEvidenceCommand command, CancellationToken cancellationToken)
    {
        var supplierId = new SupplierId(command.SupplierId);
        var requirementId = new RequirementId(command.RequirementId);
        var documentId = new DocumentId(command.DocumentId);

        var state = await repository.FindAsync(supplierId, cancellationToken)
            ?? throw new SupplierComplianceStateNotFoundException(supplierId);

        // The aggregate refuses to attach the same document twice, which is right — but on a
        // redelivery that would throw forever and dead-letter a message that was already handled
        // correctly. Recognising "already applied" here keeps at-least-once delivery harmless.
        if (state.FindObligation(requirementId)?.CurrentEvidence?.DocumentId == documentId)
        {
            return;
        }

        var evidence = new CertificateEvidence(
            documentId,
            command.CertificateNumber,
            command.Issuer,
            command.HolderName,
            new ValidityPeriod(command.IssuedOn, command.ExpiresOn),
            command.ApprovedBy,
            command.ApprovedAt);

        state.ApplyApprovedEvidence(requirementId, evidence, clock.Today, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

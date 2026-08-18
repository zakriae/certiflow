using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;

namespace Certiflow.Compliance.Application.Evidence;

/// <summary>
/// Moves an obligation to AwaitingReview because a document was submitted against it.
/// Raised from BC2's <c>DocumentStored</c>.
/// </summary>
public sealed record RecordSubmissionCommand(Guid SupplierId, Guid RequirementId, Guid DocumentId) : IRequest;

public sealed class RecordSubmissionValidator : AbstractValidator<RecordSubmissionCommand>
{
    public RecordSubmissionValidator()
    {
        RuleFor(c => c.SupplierId).NotEmpty();
        RuleFor(c => c.RequirementId).NotEmpty();
        RuleFor(c => c.DocumentId).NotEmpty();
    }
}

public sealed class RecordSubmissionHandler(
    ISupplierComplianceRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<RecordSubmissionCommand>
{
    public async Task Handle(RecordSubmissionCommand command, CancellationToken cancellationToken)
    {
        var supplierId = new SupplierId(command.SupplierId);

        var state = await repository.FindAsync(supplierId, cancellationToken)
            ?? throw new SupplierComplianceStateNotFoundException(supplierId);

        state.RecordSubmission(
            new RequirementId(command.RequirementId),
            new DocumentId(command.DocumentId),
            clock.Today,
            clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// A submission ended without approval — rejected, quarantined or superseded. Raised from BC4's
/// <c>DocumentRejected</c> and BC2's <c>DocumentSuperseded</c>.
/// </summary>
public sealed record ClearSubmissionCommand(Guid SupplierId, Guid RequirementId) : IRequest;

public sealed class ClearSubmissionValidator : AbstractValidator<ClearSubmissionCommand>
{
    public ClearSubmissionValidator()
    {
        RuleFor(c => c.SupplierId).NotEmpty();
        RuleFor(c => c.RequirementId).NotEmpty();
    }
}

public sealed class ClearSubmissionHandler(
    ISupplierComplianceRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<ClearSubmissionCommand>
{
    public async Task Handle(ClearSubmissionCommand command, CancellationToken cancellationToken)
    {
        var supplierId = new SupplierId(command.SupplierId);

        var state = await repository.FindAsync(supplierId, cancellationToken)
            ?? throw new SupplierComplianceStateNotFoundException(supplierId);

        state.ClearSubmission(new RequirementId(command.RequirementId), clock.Today, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

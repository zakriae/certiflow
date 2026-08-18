using Certiflow.Compliance.Application.Abstractions;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;

namespace Certiflow.Compliance.Application.Suppliers;

/// <summary>
/// Rebuilds obligations for every supplier in a category from a newly published profile version.
/// Raised from BC1's <c>ComplianceProfileVersionPublished</c> (FR-1.4, FR-5.1).
/// </summary>
public sealed record ApplyProfileVersionCommand(
    Guid CategoryId,
    int ProfileVersion,
    IReadOnlyList<RequirementDefinition> Requirements) : IRequest;

public sealed class ApplyProfileVersionValidator : AbstractValidator<ApplyProfileVersionCommand>
{
    public ApplyProfileVersionValidator()
    {
        RuleFor(c => c.CategoryId).NotEmpty();
        RuleFor(c => c.ProfileVersion).GreaterThan(0);
        RuleFor(c => c.Requirements).NotEmpty();

        RuleForEach(c => c.Requirements).ChildRules(requirement =>
        {
            requirement.RuleFor(r => r.RequirementId).NotEmpty();
            requirement.RuleFor(r => r.DocumentType).NotEmpty();
            requirement.RuleFor(r => r.RenewalLeadTimeDays).InclusiveBetween(1, 365);
            requirement.RuleFor(r => r.MinValidityDays).InclusiveBetween(0, 365);
        });
    }
}

public sealed class ApplyProfileVersionHandler(
    ISupplierComplianceRepository repository,
    IComplianceProfileStore profileStore,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<ApplyProfileVersionCommand>
{
    public async Task Handle(ApplyProfileVersionCommand command, CancellationToken cancellationToken)
    {
        // Stored first, so a supplier registered after this point still gets the current rules even
        // though the event itself is long gone.
        await profileStore.SaveAsync(
            new ProfileVersionSnapshot(command.CategoryId, command.ProfileVersion, command.Requirements),
            cancellationToken);

        var specifications = command.Requirements.Select(r => r.ToSpecification()).ToList();
        var supplierIds = await repository.ListSupplierIdsInCategoryAsync(command.CategoryId, cancellationToken);

        foreach (var supplierId in supplierIds)
        {
            var state = await repository.FindAsync(supplierId, cancellationToken);

            if (state is null)
            {
                // Listed a moment ago and gone now. Nothing to rebuild, and nothing worth failing
                // the whole category over.
                continue;
            }

            // The aggregate ignores an older version than the one it already holds, so replaying
            // this command out of order is safe (SRS §5.3, at-least-once delivery).
            state.ApplyProfileVersion(command.ProfileVersion, specifications, clock.Today, clock.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

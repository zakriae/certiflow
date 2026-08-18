using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;

namespace Certiflow.Compliance.Application.Suppliers;

/// <summary>
/// Creates this service's compliance state for a newly registered supplier.
/// Raised from BC1's <c>SupplierRegistered</c> / <c>SupplierActivated</c>.
/// </summary>
public sealed record RegisterSupplierComplianceCommand(Guid SupplierId, Guid CategoryId) : IRequest;

public sealed class RegisterSupplierComplianceValidator : AbstractValidator<RegisterSupplierComplianceCommand>
{
    public RegisterSupplierComplianceValidator()
    {
        RuleFor(c => c.SupplierId).NotEmpty();
        RuleFor(c => c.CategoryId).NotEmpty();
    }
}

public sealed class RegisterSupplierComplianceHandler(
    ISupplierComplianceRepository repository,
    IComplianceProfileStore profileStore,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<RegisterSupplierComplianceCommand>
{
    public async Task Handle(RegisterSupplierComplianceCommand command, CancellationToken cancellationToken)
    {
        var supplierId = new SupplierId(command.SupplierId);

        // Idempotent by state rather than by message id. The inbox in Infrastructure is the primary
        // defence against redelivery; this is the cheap check that keeps a missed dedupe from
        // throwing a duplicate-key error forever and dead-lettering a message that was fine.
        if (await repository.FindAsync(supplierId, cancellationToken) is not null)
        {
            return;
        }

        var state = SupplierComplianceState.Register(supplierId, command.CategoryId);

        // If the category's profile was published before this supplier existed, that event is gone.
        // Applying the stored snapshot here is what makes the two events order-independent.
        var profile = await profileStore.FindLatestAsync(command.CategoryId, cancellationToken);

        if (profile is not null)
        {
            state.ApplyProfileVersion(
                profile.ProfileVersion,
                [.. profile.Requirements.Select(r => r.ToSpecification())],
                clock.Today,
                clock.UtcNow);
        }

        await repository.AddAsync(state, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

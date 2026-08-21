using Certiflow.SupplierRegistry.Application.Abstractions;
using Certiflow.SupplierRegistry.Domain;
using FluentValidation;
using MediatR;

namespace Certiflow.SupplierRegistry.Application.Suppliers;

/// <summary>
/// Registers a supplier and, when a category is given, activates it so the rest of the system hears
/// about it. Activation is what makes a supplier real to Compliance.
/// </summary>
public sealed record RegisterSupplierCommand(
    string LegalName,
    string? TradingName,
    string RegistrationNumber,
    string CountryCode,
    Guid? CategoryId,
    string ContactName,
    string ContactEmail) : IRequest<Guid>;

public sealed class RegisterSupplierValidator : AbstractValidator<RegisterSupplierCommand>
{
    public RegisterSupplierValidator()
    {
        RuleFor(c => c.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.RegistrationNumber).NotEmpty();
        RuleFor(c => c.CountryCode).NotEmpty().Length(2);
        RuleFor(c => c.ContactName).NotEmpty();
        RuleFor(c => c.ContactEmail).NotEmpty();
    }
}

public sealed class RegisterSupplierHandler(
    ISupplierRepository repository,
    IUnitOfWork unitOfWork) : IRequestHandler<RegisterSupplierCommand, Guid>
{
    public async Task<Guid> Handle(RegisterSupplierCommand command, CancellationToken cancellationToken)
    {
        var registration = RegistrationNumber.Parse(command.RegistrationNumber);
        var country = CountryCode.Parse(command.CountryCode);

        // Idempotent on the natural key rather than the surrogate one, so re-running the seeder
        // does not create a second copy of every supplier.
        if (await repository.FindByRegistrationAsync(registration, country, cancellationToken) is { } existing)
        {
            return existing.Id.Value;
        }

        var supplier = Supplier.Register(
            command.LegalName,
            command.TradingName,
            registration,
            country,
            command.CategoryId is { } category ? new CategoryId(category) : null);

        // The first contact becomes primary automatically, which is what lets the supplier activate.
        supplier.AddContact(command.ContactName, EmailAddress.Parse(command.ContactEmail));

        if (command.CategoryId is not null)
        {
            supplier.Activate();
        }

        await repository.AddAsync(supplier, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return supplier.Id.Value;
    }
}

public sealed record RequirementInput(
    string DocumentType,
    bool IsMandatory,
    int RenewalLeadTimeDays,
    int MinValidityDays,
    bool RequiresIssuerMatch,
    IReadOnlyList<string>? AcceptedIssuers);

/// <summary>
/// Defines a category's compliance profile and publishes a version of it (FR-1.2, FR-1.4).
/// <para>
/// Publishing is what tells Compliance which obligations a supplier of this category carries, and
/// tells Intelligence which issuers each requirement accepts.
/// </para>
/// </summary>
public sealed record PublishProfileCommand(
    Guid CategoryId,
    string Name,
    IReadOnlyList<RequirementInput> Requirements) : IRequest<int>;

public sealed class PublishProfileValidator : AbstractValidator<PublishProfileCommand>
{
    public PublishProfileValidator()
    {
        RuleFor(c => c.CategoryId).NotEmpty();
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Requirements).NotEmpty();
    }
}

public sealed class PublishProfileHandler(
    IComplianceProfileRepository repository,
    IUnitOfWork unitOfWork,
    Certiflow.SharedKernel.IClock clock) : IRequestHandler<PublishProfileCommand, int>
{
    public async Task<int> Handle(PublishProfileCommand command, CancellationToken cancellationToken)
    {
        var categoryId = new CategoryId(command.CategoryId);
        var profile = await repository.FindAsync(categoryId, cancellationToken);

        if (profile is null)
        {
            profile = ComplianceProfile.CreateFor(categoryId, command.Name);
            await repository.AddAsync(profile, cancellationToken);
        }

        foreach (var requirement in command.Requirements)
        {
            var documentType = DocumentType.Parse(requirement.DocumentType);

            // Re-running the seeder must not fail on a requirement that already exists, and must
            // not silently create a duplicate the aggregate would refuse anyway.
            if (profile.ActiveRequirements.Any(r => r.DocumentType.IsSameAs(documentType)))
            {
                continue;
            }

            profile.AddRequirement(
                documentType,
                requirement.IsMandatory,
                requirement.RenewalLeadTimeDays,
                requirement.MinValidityDays,
                requirement.RequiresIssuerMatch,
                requirement.AcceptedIssuers);
        }

        profile.Publish(clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return profile.PublishedVersion;
    }
}

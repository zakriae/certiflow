using System.Text.Json;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Certiflow.SupplierRegistry.Application.Abstractions;
using Certiflow.SupplierRegistry.Domain;
using Microsoft.EntityFrameworkCore;
using Contracts = Certiflow.Contracts;
using DomainEvents = Certiflow.SupplierRegistry.Domain.Events;

namespace Certiflow.SupplierRegistry.Infrastructure.Persistence;

public sealed class SupplierRepository(RegistryDbContext context) : ISupplierRepository
{
    public async Task<Supplier?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken) =>
        await context.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

    public async Task<Supplier?> FindByRegistrationAsync(
        RegistrationNumber registrationNumber,
        CountryCode country,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registrationNumber);
        ArgumentNullException.ThrowIfNull(country);

        return await context.Suppliers.FirstOrDefaultAsync(
            s => s.RegistrationNumber.Normalized == registrationNumber.Normalized
              && s.Country.Value == country.Value,
            cancellationToken);
    }

    public async Task AddAsync(Supplier supplier, CancellationToken cancellationToken) =>
        await context.Suppliers.AddAsync(supplier, cancellationToken);
}

public sealed class ComplianceProfileRepository(RegistryDbContext context) : IComplianceProfileRepository
{
    public async Task<ComplianceProfile?> FindAsync(CategoryId categoryId, CancellationToken cancellationToken) =>
        await context.Profiles.FirstOrDefaultAsync(p => p.Id == categoryId, cancellationToken);

    public async Task AddAsync(ComplianceProfile profile, CancellationToken cancellationToken) =>
        await context.Profiles.AddAsync(profile, cancellationToken);
}

/// <summary>
/// Commits and drains domain events into the outbox.
/// <para>
/// BC1 is upstream of almost everything: <c>SupplierActivated</c> is what makes a supplier exist to
/// Compliance, and <c>ComplianceProfileVersionPublished</c> carries the whole requirement set —
/// including accepted issuers — so Compliance and Intelligence never have to query this service
/// (SRS §4.3, Published Language).
/// </para>
/// </summary>
public sealed class RegistryUnitOfWork(RegistryDbContext context, IClock clock) : IUnitOfWork
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var suppliers = context.ChangeTracker.Entries<Supplier>().Select(e => e.Entity).ToList();
        var profiles = context.ChangeTracker.Entries<ComplianceProfile>().Select(e => e.Entity).ToList();

        foreach (var domainEvent in suppliers.SelectMany(s => s.DomainEvents)
                     .Concat(profiles.SelectMany(p => p.DomainEvents)))
        {
            if (Translate(domainEvent, suppliers) is { } integrationEvent)
            {
                context.Outbox.Add(ToOutboxMessage(integrationEvent));
            }
        }

        var written = await context.SaveChangesAsync(cancellationToken);

        foreach (var aggregate in suppliers.Cast<object>().Concat(profiles))
        {
            switch (aggregate)
            {
                case Supplier supplier: supplier.ClearDomainEvents(); break;
                case ComplianceProfile profile: profile.ClearDomainEvents(); break;
            }
        }

        return written;
    }

    /// <summary>
    /// Takes the aggregates as well as the event, so <c>SupplierRegistered</c> can carry the primary
    /// contact. The domain event is raised inside <c>Register</c>, before any contact has been
    /// added, so the contact is not on the event - but it is on the aggregate by the time this runs,
    /// which is the same trick BC5 uses to put an obligation snapshot on a status change.
    /// </summary>
    private static Contracts.IIntegrationEvent? Translate(
        IDomainEvent domainEvent,
        IReadOnlyCollection<Supplier> suppliers) => domainEvent switch
    {
        DomainEvents.SupplierRegistered registered => new Contracts.SupplierRegistered(
            registered.SupplierId.Value,
            registered.LegalName,
            registered.TradingName,
            registered.CategoryId?.Value ?? Guid.Empty,
            registered.CountryCode,
            suppliers.SingleOrDefault(s => s.Id == registered.SupplierId)?.PrimaryContact?.Name,
            suppliers.SingleOrDefault(s => s.Id == registered.SupplierId)?.PrimaryContact?.Email.Value,
            CorrelationId: registered.EventId),

        // Activation, not registration, is what tells Compliance to start tracking a supplier: a
        // draft with no category has no obligations to track.
        DomainEvents.SupplierActivated activated => new Contracts.SupplierActivated(
            activated.SupplierId.Value,
            activated.CategoryId.Value,
            CorrelationId: activated.EventId),

        DomainEvents.SupplierCategoryChanged changed => new Contracts.SupplierCategoryChanged(
            changed.SupplierId.Value,
            changed.PreviousCategoryId.Value,
            changed.NewCategoryId.Value,
            CorrelationId: changed.EventId),

        DomainEvents.SupplierSuspended suspended => new Contracts.SupplierSuspended(
            suspended.SupplierId.Value,
            suspended.Reason,
            CorrelationId: suspended.EventId),

        DomainEvents.ComplianceProfileVersionPublished published => new Contracts.ComplianceProfileVersionPublished(
            published.CategoryId.Value,
            published.CategoryName,
            published.ProfileVersion,
            [.. published.Requirements.Select(r => new Contracts.RequirementDescriptor(
                r.RequirementId.Value,
                r.DocumentType,
                r.IsMandatory,
                r.RenewalLeadTimeDays,
                r.MinValidityDays,
                r.RequiresIssuerMatch,
                r.AcceptedIssuers,
                r.AutoAcceptThreshold))],
            CorrelationId: published.EventId),

        _ => null,
    };

    private OutboxMessage ToOutboxMessage(Contracts.IIntegrationEvent integrationEvent) => new(
        integrationEvent.EventId,
        integrationEvent.CorrelationId,
        integrationEvent.GetType().FullName!,
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJson),
        clock.UtcNow);
}

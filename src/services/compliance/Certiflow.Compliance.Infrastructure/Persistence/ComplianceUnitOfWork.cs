using System.Text.Json;
using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Contracts = Certiflow.Contracts;
using DomainEvents = Certiflow.Compliance.Domain.Events;

namespace Certiflow.Compliance.Infrastructure.Persistence;

/// <summary>
/// Commits the unit of work, refreshes the queryable status column, and drains domain events into
/// the outbox — all in one transaction.
/// </summary>
public sealed class ComplianceUnitOfWork(ComplianceDbContext context, IClock clock) : IUnitOfWork
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var aggregates = context.ChangeTracker
            .Entries<SupplierComplianceState>()
            .ToList();

        foreach (var entry in aggregates)
        {
            // The derived status is written to its column here and nowhere else, so the cache can
            // never be set by anything other than the derivation it caches (ADR-0001).
            entry.Property<string>("overall_status").CurrentValue = entry.Entity.OverallStatus.ToString();

            foreach (var domainEvent in entry.Entity.DomainEvents)
            {
                if (Translate(domainEvent, entry.Entity) is { } integrationEvent)
                {
                    context.Outbox.Add(ToOutboxMessage(integrationEvent));
                }
            }
        }

        var written = await context.SaveChangesAsync(cancellationToken);

        foreach (var entry in aggregates)
        {
            entry.Entity.ClearDomainEvents();
        }

        return written;
    }

    private static Contracts.IIntegrationEvent? Translate(IDomainEvent domainEvent, SupplierComplianceState state) =>
        domainEvent switch
        {
            DomainEvents.ComplianceStatusChanged changed => new Contracts.ComplianceStatusChanged(
                changed.SupplierId.Value,
                changed.PreviousStatus.ToString(),
                changed.NewStatus.ToString(),
                DateTimeOffset.UtcNow,
                [.. state.Obligations.Where(o => o.IsApplicable).Select(o => Snapshot(o, changed.EvaluatedOn))],
                CorrelationId: changed.EventId),

            DomainEvents.CertificateExpiringSoon expiring => new Contracts.CertificateExpiringSoon(
                expiring.SupplierId.Value,
                expiring.RequirementId.Value,
                expiring.DocumentId.Value,
                state.FindObligation(expiring.RequirementId)?.DocumentType ?? "unknown",
                expiring.ExpiresOn,
                expiring.DaysRemaining,
                CorrelationId: expiring.EventId),

            DomainEvents.CertificateExpired expired => new Contracts.CertificateExpired(
                expired.SupplierId.Value,
                expired.RequirementId.Value,
                expired.DocumentId.Value,
                state.FindObligation(expired.RequirementId)?.DocumentType ?? "unknown",
                expired.ExpiredOn,
                CorrelationId: expired.EventId),

            _ => null,
        };

    private static Contracts.ObligationSnapshot Snapshot(Obligation obligation, DateOnly today) => new(
        obligation.Id.Value,
        obligation.DocumentType,
        obligation.IsMandatory,
        obligation.Status.ToString(),
        obligation.CurrentEvidence?.DocumentId.Value,
        obligation.CurrentEvidence?.Validity.ExpiresOn,
        obligation.DaysRemaining(today));

    private OutboxMessage ToOutboxMessage(Contracts.IIntegrationEvent integrationEvent) => new(
        integrationEvent.EventId,
        integrationEvent.CorrelationId,
        integrationEvent.GetType().FullName!,
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJson),
        clock.UtcNow);
}

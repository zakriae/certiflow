using System.Text.Json;
using Certiflow.Persistence;
using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Contracts = Certiflow.Contracts;
using DomainEvents = Certiflow.Reporting.Domain.Events;

namespace Certiflow.Reporting.Infrastructure.Persistence;

public sealed class ReportRepository(ReportingDbContext context) : IReportRepository
{
    public async Task<Report?> FindAsync(ReportId id, CancellationToken cancellationToken) =>
        await context.Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(Report report) => context.Reports.Add(report);
}

/// <summary>
/// Commits and drains domain events into the outbox in one transaction, so a completed report and
/// the <c>ReportGenerated</c> announcing it cannot disagree.
/// </summary>
public sealed class ReportingUnitOfWork(ReportingDbContext context, IClock clock) : IUnitOfWork
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var aggregates = context.ChangeTracker.Entries<Report>().ToList();

        foreach (var entry in aggregates)
        {
            foreach (var domainEvent in entry.Entity.DomainEvents)
            {
                if (Translate(domainEvent) is { } integrationEvent)
                {
                    context.Outbox.Add(ToOutboxMessage(integrationEvent));
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        foreach (var entry in aggregates)
        {
            entry.Entity.ClearDomainEvents();
        }
    }

    private static Contracts.IIntegrationEvent? Translate(IDomainEvent domainEvent) => domainEvent switch
    {
        DomainEvents.ReportCompleted completed => new Contracts.ReportGenerated(
            completed.ReportId.Value,
            completed.Type.ToString(),
            completed.Subject.Value,
            completed.Storage.Container,
            completed.Storage.BlobPath,
            completed.VerificationHash,
            completed.RequestedBy,
            CorrelationId: completed.EventId),

        DomainEvents.ReportRequested requested => new Contracts.ReportRequested(
            requested.ReportId.Value,
            requested.Type.ToString(),
            requested.Subject.Value,
            requested.RequestedBy,
            CorrelationId: requested.EventId),

        _ => null,
    };

    // EventType is the full type name, not the short one: the dispatcher resolves the CLR type
    // from this string to publish it, and short names are ambiguous across contexts.
    private OutboxMessage ToOutboxMessage(Contracts.IIntegrationEvent integrationEvent) => new(
        integrationEvent.EventId,
        integrationEvent.CorrelationId,
        integrationEvent.GetType().FullName!,
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJson),
        clock.UtcNow);
}

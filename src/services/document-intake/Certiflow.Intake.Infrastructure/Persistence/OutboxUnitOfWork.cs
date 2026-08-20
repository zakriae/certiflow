using System.Text.Json;
using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Domain;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Contracts = Certiflow.Contracts;
using DomainEvents = Certiflow.Intake.Domain.Events;

namespace Certiflow.Intake.Infrastructure.Persistence;

/// <summary>
/// Commits the unit of work and drains every domain event into the outbox in the same transaction.
/// <para>
/// This is the translation boundary. Aggregates raise events in Intake's own language using
/// Intake's own types; those are converted into the Published Language of
/// <c>Certiflow.Contracts</c> here, at the edge, and nowhere else. That is why neither the domain
/// nor the application layer references Contracts, and why an architecture test enforces it
/// (ADR-0004).
/// </para>
/// </summary>
public sealed class OutboxUnitOfWork(IntakeDbContext context, IClock clock) : IUnitOfWork
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var aggregates = context.ChangeTracker
            .Entries<Document>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                if (Translate(domainEvent) is { } integrationEvent)
                {
                    context.Outbox.Add(ToOutboxMessage(integrationEvent));
                }
            }
        }

        // One SaveChanges, one transaction, both tables. Splitting these into two saves would
        // reintroduce exactly the window the outbox exists to close.
        var written = await context.SaveChangesAsync(cancellationToken);

        // Cleared only after the save succeeded. Clearing first would lose the events on a
        // concurrency conflict or a retry.
        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        return written;
    }

    /// <summary>
    /// Maps an Intake domain event to its integration counterpart.
    /// <para>
    /// Not every domain event travels. <c>DocumentReceived</c> is internal bookkeeping that no
    /// other context has a use for, and publishing it would widen this service's public surface
    /// for no reason — returning null here is a deliberate answer, not a gap.
    /// </para>
    /// </summary>
    private static Contracts.IIntegrationEvent? Translate(IDomainEvent domainEvent) => domainEvent switch
    {
        DomainEvents.DocumentStored stored => new Contracts.DocumentStored(
            stored.DocumentId.Value,
            stored.SupplierId.Value,
            stored.RequirementId?.Value,
            stored.ExpectedDocumentType,
            stored.FileName,
            stored.ContentType,
            stored.SizeBytes,
            stored.Sha256,
            stored.StorageContainer,
            stored.StorageBlobPath,
            stored.PageCount,
            stored.UploadedBy,
            // Correlation flows from the ambient request in the API layer; until that is wired the
            // event id doubles as its own correlation so nothing is ever left empty.
            CorrelationId: stored.EventId),

        DomainEvents.DocumentQuarantined quarantined => new Contracts.DocumentQuarantined(
            quarantined.DocumentId.Value,
            quarantined.SupplierId.Value,
            quarantined.RequirementId?.Value,
            quarantined.Reason,
            CorrelationId: quarantined.EventId),

        DomainEvents.DocumentSuperseded superseded => new Contracts.DocumentSuperseded(
            superseded.SupersededDocumentId.Value,
            superseded.SupersedingDocumentId.Value,
            superseded.SupplierId.Value,
            superseded.RequirementId.Value,
            CorrelationId: superseded.EventId),

        _ => null,
    };

    private OutboxMessage ToOutboxMessage(Contracts.IIntegrationEvent integrationEvent) => new(
        integrationEvent.EventId,
        integrationEvent.CorrelationId,
        integrationEvent.GetType().FullName
            ?? throw new InvalidOperationException("Integration events must be named types."),
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJson),
        clock.UtcNow);
}

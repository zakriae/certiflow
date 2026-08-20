using System.Text.Json;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Certiflow.Verification.Application.Abstractions;
using Certiflow.Verification.Domain;
using Microsoft.EntityFrameworkCore;
using Contracts = Certiflow.Contracts;
using DomainEvents = Certiflow.Verification.Domain.Events;

namespace Certiflow.Verification.Infrastructure.Persistence;

/// <summary>
/// Commits the unit of work and drains domain events into the outbox in the same transaction.
/// <para>
/// The translation boundary for BC4. <c>DocumentApproved</c> is the most consequential event in the
/// system — it is what makes a document count as evidence — so it must be impossible for the
/// verdict to be stored without the event, or the event to go out for a verdict that rolled back.
/// </para>
/// </summary>
public sealed class VerificationUnitOfWork(VerificationDbContext context, IClock clock) : IUnitOfWork
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var aggregates = context.ChangeTracker
            .Entries<ReviewTask>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                if (Translate(domainEvent, aggregate) is { } integrationEvent)
                {
                    context.Outbox.Add(ToOutboxMessage(integrationEvent));
                }
            }
        }

        var written = await context.SaveChangesAsync(cancellationToken);

        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        return written;
    }

    private static Contracts.IIntegrationEvent? Translate(IDomainEvent domainEvent, ReviewTask task) => domainEvent switch
    {
        DomainEvents.ReviewTaskRaised raised => new Contracts.ReviewTaskRaised(
            raised.ReviewTaskId.Value,
            raised.DocumentId.Value,
            task.ExtractionJobId.Value,
            raised.SupplierId.Value,
            raised.RequirementId.Value,
            raised.Reason.ToString(),
            raised.OverallConfidence,
            CorrelationId: raised.EventId),

        // The approved field values travel with the event because they are the reviewer's
        // *accepted* values, which may differ from what the model extracted. BC5 must never reach
        // back into an extraction to find out what was approved.
        DomainEvents.DocumentApproved approved => new Contracts.DocumentApproved(
            approved.ReviewTaskId.Value,
            approved.DocumentId.Value,
            approved.SupplierId.Value,
            approved.RequirementId.Value,
            approved.DocumentType,
            Value(approved.AcceptedValues, "holderName"),
            Value(approved.AcceptedValues, "issuerName"),
            Value(approved.AcceptedValues, "certificateNumber"),
            Date(approved.AcceptedValues, "issuedOn"),
            Date(approved.AcceptedValues, "expiresOn"),
            approved.AcceptedValues.GetValueOrDefault("scope"),
            approved.ApprovedBy,
            approved.ApprovedAt,
            CorrelationId: approved.EventId),

        DomainEvents.DocumentRejected rejected => new Contracts.DocumentRejected(
            rejected.ReviewTaskId.Value,
            rejected.DocumentId.Value,
            rejected.SupplierId.Value,
            rejected.RequirementId.Value,
            rejected.Reason.ToString(),
            rejected.ReasonNote,
            rejected.RejectedBy,
            rejected.RejectedAt,
            CorrelationId: rejected.EventId),

        DomainEvents.ReviewTaskCancelled cancelled => new Contracts.ReviewTaskCancelled(
            cancelled.ReviewTaskId.Value,
            cancelled.DocumentId.Value,
            cancelled.Reason,
            CorrelationId: cancelled.EventId),

        _ => null,
    };

    private static string Value(IReadOnlyDictionary<string, string> accepted, string field) =>
        accepted.GetValueOrDefault(field)
        ?? throw new InvalidOperationException(
            $"Approved document is missing '{field}'. The aggregate should have refused approval.");

    /// <summary>
    /// Dates are normalised to ISO by the extraction pipeline and confirmed by the reviewer, so a
    /// value that will not parse here means something upstream is wrong — and recording evidence
    /// with a guessed date is worse than failing loudly.
    /// </summary>
    private static DateOnly Date(IReadOnlyDictionary<string, string> accepted, string field) =>
        DateOnly.TryParse(Value(accepted, field), System.Globalization.CultureInfo.InvariantCulture, out var date)
            ? date
            : throw new InvalidOperationException($"Approved value for '{field}' is not a date.");

    private OutboxMessage ToOutboxMessage(Contracts.IIntegrationEvent integrationEvent) => new(
        integrationEvent.EventId,
        integrationEvent.CorrelationId,
        integrationEvent.GetType().FullName!,
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJson),
        clock.UtcNow);
}

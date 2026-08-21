using Certiflow.Contracts;
using Certiflow.Intelligence.Infrastructure.Persistence;
using Certiflow.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Intelligence.Infrastructure.Messaging;

/// <summary>
/// Keeps Document Intelligence's copy of the supplier list current.
/// <para>
/// Without it the entity-match signal has no supplier name to compare a certificate's holder
/// against, and the check that catches "issued to the wrong company" cannot fire.
/// </para>
/// </summary>
public sealed class SupplierRegisteredIntelligenceConsumer(IntelligenceDbContext database)
    : IConsumer<SupplierRegistered>
{
    public async Task Consume(ConsumeContext<SupplierRegistered> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        var existing = await database.Suppliers
            .FirstOrDefaultAsync(s => s.SupplierId == message.SupplierId, cancellationToken);

        // Upsert rather than insert-and-dedupe: a supplier renaming itself is a legitimate repeat
        // of this event, and the read model should track the current name.
        if (existing is null)
        {
            database.Suppliers.Add(new SupplierRecord(message.SupplierId, message.LegalName, message.TradingName));
        }
        else
        {
            existing.Update(message.LegalName, message.TradingName);
        }

        await database.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Keeps the requirement copy current, including each one's accepted issuers and its own
/// auto-accept threshold (FR-5.6).
/// </summary>
public sealed class ProfilePublishedIntelligenceConsumer(IntelligenceDbContext database)
    : IConsumer<ComplianceProfileVersionPublished>
{
    public async Task Consume(ConsumeContext<ComplianceProfileVersionPublished> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        foreach (var descriptor in message.Requirements)
        {
            var existing = await database.Requirements
                .FirstOrDefaultAsync(r => r.RequirementId == descriptor.RequirementId, cancellationToken);

            if (existing is null)
            {
                database.Requirements.Add(new RequirementRecord(
                    descriptor.RequirementId,
                    message.CategoryId,
                    descriptor.DocumentType,
                    descriptor.RequiresIssuerMatch,
                    descriptor.AcceptedIssuers,
                    descriptor.AutoAcceptThreshold));

                continue;
            }

            existing.Update(
                descriptor.DocumentType,
                descriptor.RequiresIssuerMatch,
                descriptor.AcceptedIssuers,
                descriptor.AutoAcceptThreshold);
        }

        await database.SaveChangesAsync(cancellationToken);
    }
}

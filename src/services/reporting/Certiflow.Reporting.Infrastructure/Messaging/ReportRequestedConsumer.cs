using Certiflow.Persistence;
using Certiflow.Reporting.Application.Generation;
using Certiflow.Reporting.Infrastructure.Persistence;
using Contracts = Certiflow.Contracts;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Certiflow.Reporting.Infrastructure.Messaging;

/// <summary>
/// Picks up an accepted request and generates it (FR-6.4).
/// <para>
/// The message comes from this service's own outbox rather than from another context. That is what
/// makes the API's <c>202</c> honest: the job row and the message that will act on it are written
/// in the same transaction, so a crash immediately after "accepted" leaves work queued rather than
/// a request that was acknowledged and then evaporated.
/// </para>
/// </summary>
public sealed class ReportRequestedConsumer(ReportingDbContext database, ISender sender)
    : IConsumer<Contracts.ReportRequested>
{
    public async Task Consume(ConsumeContext<Contracts.ReportRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var messageId = context.Message.EventId;

        if (await database.Inbox.AnyAsync(m => m.MessageId == messageId, context.CancellationToken))
        {
            return;
        }

        await sender.Send(new GenerateReportCommand(context.Message.ReportId), context.CancellationToken);

        database.Inbox.Add(new InboxMessage(messageId, nameof(Contracts.ReportRequested), DateTimeOffset.UtcNow));
        await database.SaveChangesAsync(context.CancellationToken);
    }
}

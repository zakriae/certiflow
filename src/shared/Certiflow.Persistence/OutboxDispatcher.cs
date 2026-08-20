using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Certiflow.Persistence;

/// <summary>
/// A <c>DbContext</c> that carries an outbox. Implemented by every publishing service's context.
/// </summary>
public interface IOutboxContext
{
    DbSet<OutboxMessage> Outbox { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// How often to look for pending messages. Polling rather than a database notification
    /// deliberately: it is one query on a filtered index, it behaves identically on SQL Server and
    /// Azure SQL, and it cannot miss a message the way a dropped notification can.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// After this many failures a message stops being retried and waits for a human. Without a
    /// ceiling, one permanently unpublishable message retries forever and buries every log.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;
}

/// <summary>
/// Publishes committed outbox messages to the broker.
/// <para>
/// The second half of the transactional outbox. The first half — writing the event in the same
/// transaction as the state change — guarantees the event <em>exists</em>; this guarantees it
/// eventually <em>leaves</em>. Neither half is useful alone.
/// </para>
/// <para>
/// <b>Delivery is at-least-once and cannot be made exactly-once here.</b> If the process dies
/// between publishing to the broker and marking the row published, the message goes out again on
/// restart. That is a property to handle, not a bug to fix: consumers deduplicate on the message
/// id, which is why every integration event carries one (SRS §5.3, §19 Q6).
/// </para>
/// <para>
/// Generic over the context because every publishing service needs exactly this and none of them
/// needs it to differ.
/// </para>
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext, IOutboxContext
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly OutboxDispatcherOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failure here is the dispatcher's problem, not the message's. Swallowing keeps
                // the loop alive; letting it escape would stop the background service and silently
                // strand every future event.
                OutboxLog.CycleFailed(logger, exception);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var pending = await context.Outbox
            .Where(m => m.PublishedAt == null && m.PublishAttempts < _options.MaxAttempts)
            .OrderBy(m => m.OccurredAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var message in pending)
        {
            try
            {
                var contractType = ResolveContractType(message.EventType);

                var payload = JsonSerializer.Deserialize(message.PayloadJson, contractType, PayloadJson)
                    ?? throw new InvalidOperationException($"Outbox message {message.EventId} deserialised to null.");

                // Published with the stored EventId as the transport MessageId, so a redelivery is
                // recognisable as the same message rather than looking like a new one.
                await publisher.Publish(payload, contractType, publishContext =>
                {
                    publishContext.MessageId = message.EventId;
                    publishContext.CorrelationId = message.CorrelationId;
                }, cancellationToken);

                message.MarkPublished(DateTimeOffset.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.MarkFailed(exception.Message);
                OutboxLog.PublishFailed(logger, exception, message.EventId, message.EventType, message.PublishAttempts);
            }
        }

        // Saved once per batch. A crash before this replays the whole batch, which is exactly the
        // at-least-once behaviour consumers are built for.
        await context.SaveChangesAsync(cancellationToken);

        OutboxLog.BatchDispatched(logger, pending.Count(m => m.PublishedAt is not null), pending.Count);
    }

    /// <summary>
    /// Resolves a stored type name against the loaded assemblies.
    /// <para>
    /// Searched rather than assumed: the contracts assembly is the expected home, but a message
    /// whose type has moved should fail with a clear error rather than a null reference three
    /// frames later.
    /// </para>
    /// </summary>
    private static Type ResolveContractType(string eventType) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(eventType, throwOnError: false))
            .FirstOrDefault(type => type is not null)
        ?? throw new InvalidOperationException($"Unknown integration event type '{eventType}'.");
}

/// <summary>Registration helper so each service wires the dispatcher in one line.</summary>
public static class OutboxDispatcherRegistration
{
    public static IServiceCollection AddOutboxDispatcher<TContext>(this IServiceCollection services)
        where TContext : DbContext, IOutboxContext
    {
        services.AddHostedService<OutboxDispatcher<TContext>>();

        return services;
    }
}

internal static partial class OutboxLog
{
    [LoggerMessage(EventId = 9010, Level = LogLevel.Information, Message = "Outbox published {Published} of {Total} message(s)")]
    public static partial void BatchDispatched(ILogger logger, int published, int total);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Error, Message = "Outbox failed to publish {EventId} ({EventType}), attempt {Attempts}")]
    public static partial void PublishFailed(ILogger logger, Exception exception, Guid eventId, string eventType, int attempts);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Error, Message = "Outbox dispatch cycle failed")]
    public static partial void CycleFailed(ILogger logger, Exception exception);
}

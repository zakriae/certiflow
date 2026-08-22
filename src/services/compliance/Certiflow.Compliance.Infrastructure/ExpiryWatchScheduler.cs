using Certiflow.Compliance.Application.Evaluation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Certiflow.Compliance.Infrastructure;

public sealed class ExpiryWatchOptions
{
    public const string SectionName = "ExpiryWatch";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to sweep. A day in every real environment; the demo overrides it to minutes so a
    /// certificate can be seen lapsing without waiting for midnight.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Delay before the first sweep. Not zero: every replica starting at once would otherwise sweep
    /// simultaneously on deploy, which is the one moment they are guaranteed to collide.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Runs the expiry sweep on a timer (FR-5.4).
/// <para>
/// <b>Why this lives in BC5 and not BC7.</b> The SRS pencils the timer into the notification service
/// as an Azure Function, and there is a reasonable version of that. But the sweep does not send
/// anything — it re-evaluates every supplier against today's date and raises domain events when a
/// status actually changes. That is Compliance's own data and Compliance's own rules, and putting
/// the trigger in another service would mean BC7 making an authenticated HTTP call into BC5 every
/// night to ask it to do its own job. The reminders that follow are BC7's, and they already arrive
/// as events.
/// </para>
/// <para>
/// <b>What happens with two replicas.</b> Both sweep. That is survivable rather than correct: the
/// sweep only raises events where a status genuinely changed, and BC7 deduplicates reminders on
/// (document, window) with a unique index, so the second replica's events produce no second email.
/// The honest fix at real scale is a distributed lock or a single-instance timer trigger; the honest
/// note is that this one leans on a downstream guarantee rather than providing its own.
/// </para>
/// </summary>
public sealed partial class ExpiryWatchScheduler(
    IServiceScopeFactory scopes,
    IOptions<ExpiryWatchOptions> options,
    ILogger<ExpiryWatchScheduler> logger) : BackgroundService
{
    private readonly ExpiryWatchOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Disabled(logger);
            return;
        }

        try
        {
            await Task.Delay(_options.InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval);

        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var result = await sender.Send(new RunExpiryWatchCommand(), stoppingToken);

                Swept(logger, result.SuppliersEvaluated, result.SuppliersFailed, result.EvaluatedOn);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never rethrow. A BackgroundService that throws stops for good, and a reminder
                // system that quietly stopped weeks ago is worse than one that never existed -
                // nobody notices until a certificate lapses unannounced.
                SweepFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 5040, Level = LogLevel.Warning, Message = "The expiry watch is disabled; no sweep will run")]
    private static partial void Disabled(ILogger logger);

    [LoggerMessage(EventId = 5041, Level = LogLevel.Information,
        Message = "Expiry sweep for {EvaluatedOn}: {Evaluated} evaluated, {Failed} failed")]
    private static partial void Swept(ILogger logger, int evaluated, int failed, DateOnly evaluatedOn);

    [LoggerMessage(EventId = 5042, Level = LogLevel.Error, Message = "The expiry sweep failed; it will run again next interval")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}

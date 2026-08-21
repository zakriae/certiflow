using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Certiflow.Persistence;

/// <summary>
/// Brings a context's schema up to date by running its EF migrations.
/// <para>
/// <b>This replaced a development-only bootstrap, and the point is that it is not
/// development-only.</b> The old code created a schema that was missing and could not evolve one
/// that already existed, so adding a table to a live context did nothing at all — and the failure
/// only surfaced as <c>Invalid object name</c> the next time a consumer touched it. That cost real
/// time twice, and needed an escape hatch (<c>CERTIFLOW_RECREATE_SCHEMA</c>) that worked by
/// dropping every table in the schema.
/// </para>
/// <para>
/// Migrations remove the divergence: development and Azure now build the schema the same way, from
/// the same files, so a migration that is wrong is wrong on a laptop first. Each context keeps its
/// own <c>__migrations</c> history table inside its own schema, which is what makes eight contexts
/// in one database (SRS §13.1) work — they never read each other's history.
/// </para>
/// </summary>
public static partial class SchemaMigrator
{
    /// <summary>
    /// <para>
    /// <b>Deliberately not called by the services themselves in Azure.</b> Container Apps scales to
    /// several replicas, and several replicas racing to apply the same migration is how a
    /// deployment corrupts a schema. Deployment runs migrations once, as a step, before the new
    /// revision starts (NFR-19); locally there is exactly one replica, so calling it on startup is
    /// safe and saves a manual step.
    /// </para>
    /// </summary>
    public static async Task MigrateSchemaAsync<TContext>(
        this TContext context,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(context);

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        var names = pending as string[] ?? [.. pending];

        if (names.Length == 0)
        {
            return;
        }

        if (logger is not null)
        {
            // Logged by name rather than count. "Applying 3 migrations" tells you nothing when one
            // of them is the one that dropped a column.
            ApplyingMigrations(logger, names.Length, typeof(TContext).Name, string.Join(", ", names));
        }

        await context.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 9030,
        Level = LogLevel.Information,
        Message = "Applying {Count} migration(s) to {Context}: {Migrations}")]
    private static partial void ApplyingMigrations(ILogger logger, int count, string context, string migrations);
}

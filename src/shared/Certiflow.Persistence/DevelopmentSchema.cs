using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Certiflow.Persistence;

/// <summary>
/// Creates a context's tables in development.
/// <para>
/// <b>Why <c>EnsureCreated</c> is not used.</b> Eight bounded contexts share one database with a
/// schema each (SRS §13.1), and <c>EnsureCreated</c> is all-or-nothing <em>per database</em>: the
/// first service to start creates the database and its own tables, and every context after it
/// finds the database already present and creates nothing at all. That failure is silent until a
/// consumer hits <c>Invalid object name</c> at runtime — which is exactly how it was found.
/// <c>CreateTables</c> works per context, so each service can bring up its own schema.
/// </para>
/// <para>
/// <b>What this is not: a migration tool.</b> It creates a schema that is missing; it cannot
/// evolve one that already exists. Adding a table to a context whose schema is already present
/// does nothing, and the same silent <c>Invalid object name</c> appears one table later — found
/// exactly that way when BC3 gained an outbox. In development the answer is to recreate the
/// schema, which is safe because all data here is generated (NFR-11). Real environments run EF
/// migrations as a deploy step (NFR-19); letting a scaled-out service alter its own schema on
/// startup is how a deployment corrupts one.
/// </para>
/// </summary>
public static class DevelopmentSchema
{
    /// <summary>
    /// Set <c>CERTIFLOW_RECREATE_SCHEMA=true</c> to drop and rebuild the schema on startup. Needed
    /// whenever a context gains or changes a table, until migrations exist.
    /// </summary>
    public const string RecreateVariable = "CERTIFLOW_RECREATE_SCHEMA";

    public static async Task EnsureSchemaAsync<TContext>(
        this TContext context,
        string schema,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(context);

        var creator = context.Database.GetService<IRelationalDatabaseCreator>();

        if (!await creator.ExistsAsync(cancellationToken))
        {
            await creator.CreateAsync(cancellationToken);
        }

        var recreate = string.Equals(
            Environment.GetEnvironmentVariable(RecreateVariable), "true", StringComparison.OrdinalIgnoreCase);

        if (recreate)
        {
            await DropSchemaAsync(context, schema, cancellationToken);
        }

        var tables = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = {schema}")
            .SingleAsync(cancellationToken);

        if (tables == 0)
        {
            await creator.CreateTablesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Drops every table in the schema. Foreign keys go first, because a table referenced by
    /// another cannot be dropped and this makes no assumptions about ordering.
    /// </summary>
    private static async Task DropSchemaAsync(DbContext context, string schema, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @sql NVARCHAR(MAX) = N'';

            SELECT @sql += N'ALTER TABLE [' + s.name + '].[' + t.name + '] DROP CONSTRAINT [' + f.name + '];'
            FROM sys.foreign_keys f
            JOIN sys.tables t ON f.parent_object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = '{schema}';

            SELECT @sql += N'DROP TABLE [' + s.name + '].[' + t.name + '];'
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = '{schema}';

            EXEC sp_executesql @sql;
            """;

        await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }
}

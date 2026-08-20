using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Persistence;

/// <summary>
/// Creates this context's tables in development.
/// <para>
/// <b>Why <c>EnsureCreated</c> is not used.</b> Eight bounded contexts share one database with a
/// schema each (SRS §13.1), and <c>EnsureCreated</c> is all-or-nothing <em>per database</em>: the
/// first service to start creates the database and its own tables, and every context after it
/// finds the database already present and creates nothing at all. The failure is silent until a
/// consumer hits "Invalid object name '<schema>.<table>'" at runtime — which is exactly how it
/// was found.
/// </para>
/// <para>
/// <c>CreateTables</c> works per context rather than per database, so each service can bring up
/// its own schema independently. Development only: real environments run EF migrations as a deploy
/// step (NFR-19), because letting a scaled-out service alter its own schema on startup is how a
/// deployment corrupts one.
/// </para>
/// </summary>
public static class DevelopmentSchema
{
    public static async Task EnsureSchemaAsync<TContext>(this TContext context, string schema, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(context);

        var creator = context.Database.GetService<IRelationalDatabaseCreator>();

        if (!await creator.ExistsAsync(cancellationToken))
        {
            await creator.CreateAsync(cancellationToken);
        }

        var tables = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = {schema}")
            .SingleAsync(cancellationToken);

        if (tables == 0)
        {
            await creator.CreateTablesAsync(cancellationToken);
        }
    }
}

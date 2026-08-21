using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Certiflow.Reporting.Infrastructure.Persistence;

/// <summary>
/// Builds the context for <c>dotnet ef</c> only. Never used at runtime.
/// <para>
/// Explicit rather than letting EF start the API project to find one. The APIs create their schema
/// during startup, before <c>app.Run()</c> — and EF's host resolver executes everything up to that
/// point, so generating a migration would try to connect to a database that may not exist yet and
/// would be pointless even if it did. A factory that constructs nothing but options avoids the
/// whole question.
/// </para>
/// <para>
/// The connection string here is a design-time placeholder. Migrations are generated from the model,
/// not from a live database, so it is never opened; the value only has to parse.
/// </para>
/// </summary>
public sealed class ReportingDbContextFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReportingDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\certiflow-design-time;Database=certiflow",
                sql => sql.MigrationsHistoryTable("__migrations", ReportingDbContext.Schema))
            .Options;

        return new ReportingDbContext(options);
    }
}

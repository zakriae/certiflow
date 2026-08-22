using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Certiflow.Notifications.Infrastructure.Persistence;

/// <summary>Design-time only, for <c>dotnet ef</c>. See the other contexts' factories.</summary>
public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\certiflow-design-time;Database=certiflow",
                sql => sql.MigrationsHistoryTable("__migrations", NotificationsDbContext.Schema))
            .Options;

        return new NotificationsDbContext(options);
    }
}

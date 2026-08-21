using Certiflow.Intelligence.Infrastructure;
using Certiflow.Intelligence.Infrastructure.Persistence;
using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddIntelligenceInfrastructure(builder.Configuration);
builder.Services.AddIntelligenceMessaging(builder.Configuration);

var host = builder.Build();

// Development applies migrations on startup so a fresh clone needs no extra step. Azure does NOT:
// several Container Apps replicas racing to apply the same migration is how a deployment corrupts a
// schema, so deployment runs them once as a step before the new revision starts (NFR-19).
if (builder.Environment.IsDevelopment())
{
    await using var scope = host.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    await database.MigrateSchemaAsync(logger);
}

await host.RunAsync();

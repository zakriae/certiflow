using Certiflow.Intelligence.Infrastructure;
using Certiflow.Intelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddIntelligenceInfrastructure(builder.Configuration);
builder.Services.AddIntelligenceMessaging(builder.Configuration);

var host = builder.Build();

// Development convenience, as in the intake API: real environments migrate as a deploy step.
if (builder.Environment.IsDevelopment())
{
    await using var scope = host.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
    await database.EnsureSchemaAsync(IntelligenceDbContext.Schema);
}

await host.RunAsync();

using Certiflow.Persistence;
using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Infrastructure.Clients;
using Certiflow.Reporting.Infrastructure.Messaging;
using Certiflow.Reporting.Infrastructure.Persistence;
using Certiflow.Reporting.Infrastructure.Rendering;
using Certiflow.Reporting.Infrastructure.Storage;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Reporting.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReportingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<ReportingDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("ReportingDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", ReportingDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        services.Configure<ReportStorageOptions>(configuration.GetSection(ReportStorageOptions.SectionName));
        services.Configure<OutboxDispatcherOptions>(configuration.GetSection(OutboxDispatcherOptions.SectionName));

        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<IUnitOfWork, ReportingUnitOfWork>();
        services.AddScoped<IComplianceSnapshotSource, HttpComplianceSnapshotSource>();
        services.AddSingleton<IReportBlobStore, BlobReportStore>();
        services.AddSingleton<IReportRenderer, QuestPdfReportRenderer>();

        // Timeouts rather than defaults. A report that hangs for 100 seconds because Compliance is
        // wedged is a job that looks alive and is not; ten seconds is far longer than a healthy
        // answer takes and short enough that the failure is recorded while anyone still cares.
        AddServiceClient(services, HttpComplianceSnapshotSource.ComplianceClient,
            configuration["Services:Compliance"] ?? "http://localhost:5300");

        AddServiceClient(services, HttpComplianceSnapshotSource.RegistryClient,
            configuration["Services:Registry"] ?? "http://localhost:5270");

        // Not itself token-bearing, or it would need a token to get a token.
        services.AddHttpClient(ServiceTokenHandler.TokenClient, client =>
        {
            client.BaseAddress = new Uri(configuration["Auth:Authority"] ?? "http://localhost:5000");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddTransient<ServiceTokenHandler>();

        services.AddHostedService<OutboxDispatcher<ReportingDbContext>>();

        return services;
    }

    private static void AddServiceClient(IServiceCollection services, string name, string baseAddress) =>
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        // Every outbound call carries the service identity. Without it these are anonymous, and
        // every service validates JWTs now - so a report would fail with a 401 that reads like a
        // configuration mistake rather than a missing identity.
        .AddHttpMessageHandler<ServiceTokenHandler>();

    public static IServiceCollection AddReportingMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<ReportRequestedConsumer>();

            // Prefixed for the same reason Audit's queues are (ADR-0005). Nothing else consumes
            // ReportRequested today, which is exactly the situation in which the default name looks
            // harmless right up until a second service wants the same event.
            bus.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(prefix: "reporting", includeNamespace: false));

            if (!string.IsNullOrWhiteSpace(serviceBus))
            {
                bus.UsingAzureServiceBus((context, configurator) =>
                {
                    configurator.Host(serviceBus);
                    configurator.ConfigureEndpoints(context);
                });

                return;
            }

            bus.UsingRabbitMq((context, configurator) =>
            {
                configurator.Host(
                    configuration.GetConnectionString("RabbitMq") ?? "amqp://guest:guest@localhost:5672");

                configurator.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));

                // Rendering is CPU-bound and calls two services per report. Left unbounded, a burst
                // of requests would open as many concurrent HTTP calls as there are messages and
                // make the dependency outage it causes look like the dependency's fault.
                configurator.UseConcurrencyLimit(4);
                configurator.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}

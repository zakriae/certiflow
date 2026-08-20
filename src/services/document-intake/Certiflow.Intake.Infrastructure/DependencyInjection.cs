using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Infrastructure.Messaging;
using Certiflow.Intake.Infrastructure.Persistence;
using Certiflow.Intake.Infrastructure.Storage;
using Certiflow.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Intake.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntakeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<IntakeDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("IntakeDatabase"),
                sql =>
                {
                    // Migration history lives in this context's own schema, so eight contexts
                    // sharing one database do not fight over one history table (SRS §13.1).
                    sql.MigrationsHistoryTable("__migrations", IntakeDbContext.Schema);

                    // Transient SQL faults are normal in the cloud, not exceptional. Without this a
                    // routine Azure SQL failover surfaces as a failed upload.
                    sql.EnableRetryOnFailure();
                }));

        services.Configure<BlobStorageOptions>(configuration.GetSection(BlobStorageOptions.SectionName));

        services.AddSingleton<IDocumentBlobStore, BlobDocumentStore>();
        services.AddSingleton<IDocumentInspector, DocumentInspector>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IUnitOfWork, OutboxUnitOfWork>();

        return services;
    }

    /// <summary>
    /// Publishing side only - Intake consumes nothing. RabbitMQ locally, Azure Service Bus when a
    /// connection string is present; the outbox dispatcher is transport-agnostic either way.
    /// </summary>
    public static IServiceCollection AddIntakeMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();

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

                configurator.ConfigureEndpoints(context);
            });
        });

        services.Configure<OutboxDispatcherOptions>(configuration.GetSection(OutboxDispatcherOptions.SectionName));
        services.AddHostedService<OutboxDispatcher>();

        return services;
    }
}

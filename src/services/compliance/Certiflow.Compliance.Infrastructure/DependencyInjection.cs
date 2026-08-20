using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Infrastructure.Messaging;
using Certiflow.Compliance.Infrastructure.Persistence;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Compliance.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddComplianceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<ComplianceDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("ComplianceDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", ComplianceDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ISupplierComplianceRepository, SupplierComplianceRepository>();
        services.AddScoped<IComplianceProfileStore, ComplianceProfileStore>();
        services.AddScoped<IUnitOfWork, ComplianceUnitOfWork>();

        return services;
    }

    public static IServiceCollection AddComplianceMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            // Compliance is the most connected context in the system: it listens to the registry,
            // to intake, and to verification, because a supplier's status depends on all three.
            bus.AddConsumer<SupplierRegisteredConsumer>();
            bus.AddConsumer<ProfileVersionPublishedConsumer>();
            bus.AddConsumer<DocumentStoredComplianceConsumer>();
            bus.AddConsumer<DocumentApprovedConsumer>();
            bus.AddConsumer<DocumentRejectedConsumer>();
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

                configurator.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));

                configurator.ConfigureEndpoints(context);
            });
        });

        services.Configure<OutboxDispatcherOptions>(configuration.GetSection(OutboxDispatcherOptions.SectionName));
        services.AddOutboxDispatcher<ComplianceDbContext>();

        return services;
    }
}

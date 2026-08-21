using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Certiflow.SupplierRegistry.Application.Abstractions;
using Certiflow.SupplierRegistry.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.SupplierRegistry.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRegistryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<RegistryDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("RegistryDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", RegistryDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IComplianceProfileRepository, ComplianceProfileRepository>();
        services.AddScoped<IUnitOfWork, RegistryUnitOfWork>();

        return services;
    }

    /// <summary>Publishing only — the registry is upstream and consumes nothing.</summary>
    public static IServiceCollection AddRegistryMessaging(
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
        services.AddOutboxDispatcher<RegistryDbContext>();

        return services;
    }
}

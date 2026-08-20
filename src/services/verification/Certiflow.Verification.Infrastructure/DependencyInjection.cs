using Certiflow.Persistence;
using Certiflow.Verification.Application.Abstractions;
using Certiflow.Verification.Infrastructure.Messaging;
using Certiflow.Verification.Infrastructure.Persistence;
using Certiflow.SharedKernel;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Verification.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVerificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<VerificationDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("VerificationDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", VerificationDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IReviewTaskRepository, ReviewTaskRepository>();
        services.AddScoped<IUnitOfWork, VerificationUnitOfWork>();

        return services;
    }

    public static IServiceCollection AddVerificationMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<ExtractionCompletedConsumer>();
            bus.AddConsumer<DocumentSupersededConsumer>();
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
        services.AddOutboxDispatcher<VerificationDbContext>();

        return services;
    }
}

using Certiflow.Messaging;
using Certiflow.Notifications.Application.Abstractions;
using Certiflow.Notifications.Infrastructure.Delivery;
using Certiflow.Notifications.Infrastructure.Messaging;
using Certiflow.Notifications.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Notifications.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("NotificationsDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", NotificationsDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));

        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<ISupplierContactDirectory, SupplierContactDirectory>();
        services.AddSingleton<INotificationSender, InAppNotificationSender>();

        return services;
    }

    public static IServiceCollection AddNotificationsMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<SupplierRegisteredNotificationConsumer>();
            bus.AddConsumer<DocumentApprovedNotificationConsumer>();
            bus.AddConsumer<DocumentRejectedNotificationConsumer>();
            bus.AddConsumer<CertificateExpiringSoonConsumer>();
            bus.AddConsumer<CertificateExpiredConsumer>();
            bus.AddConsumer<ReportGeneratedNotificationConsumer>();

            // Prefixed, per ADR-0005. Four of these five events already have consumers elsewhere,
            // so without a prefix this service would compete with them for the same queues and each
            // message would go to exactly one of the two.
            bus.SetEndpointNameFormatter(
                new KebabCaseEndpointNameFormatter(prefix: "notifications", includeNamespace: false));

            if (!string.IsNullOrWhiteSpace(serviceBus))
            {
                bus.UsingAzureServiceBus((context, configurator) =>
                {
                    configurator.UseCertiflowHost(serviceBus);
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

        return services;
    }
}

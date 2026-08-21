using Certiflow.Messaging;
using Certiflow.Audit.Infrastructure.Messaging;
using Certiflow.Audit.Infrastructure.Persistence;
using Certiflow.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Audit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAuditInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<AuditDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("AuditDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", AuditDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        return services;
    }

    public static IServiceCollection AddAuditMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            // Every event in the catalogue, listed explicitly. This list *is* the answer to "what
            // does the audit trail record?" - a reader can check it, and adding an event type to
            // the system without adding it here is a visible omission rather than a silent one.
            bus.AddConsumer<AuditConsumer<SupplierRegistered>>();
            bus.AddConsumer<AuditConsumer<SupplierActivated>>();
            bus.AddConsumer<AuditConsumer<SupplierSuspended>>();
            bus.AddConsumer<AuditConsumer<SupplierCategoryChanged>>();
            bus.AddConsumer<AuditConsumer<ComplianceProfileVersionPublished>>();
            bus.AddConsumer<AuditConsumer<DocumentStored>>();
            bus.AddConsumer<AuditConsumer<DocumentQuarantined>>();
            bus.AddConsumer<AuditConsumer<DocumentSuperseded>>();
            bus.AddConsumer<AuditConsumer<ExtractionCompleted>>();
            bus.AddConsumer<AuditConsumer<ExtractionFailed>>();
            bus.AddConsumer<AuditConsumer<GroundingFailed>>();
            bus.AddConsumer<AuditConsumer<ReviewTaskRaised>>();
            bus.AddConsumer<AuditConsumer<ReviewTaskAssigned>>();
            bus.AddConsumer<AuditConsumer<ReviewTaskCancelled>>();
            bus.AddConsumer<AuditConsumer<FieldCorrected>>();
            bus.AddConsumer<AuditConsumer<DocumentApproved>>();
            bus.AddConsumer<AuditConsumer<DocumentRejected>>();
            bus.AddConsumer<AuditConsumer<ComplianceStatusChanged>>();
            bus.AddConsumer<AuditConsumer<CertificateExpiringSoon>>();
            bus.AddConsumer<AuditConsumer<CertificateExpired>>();
            bus.AddConsumer<AuditConsumer<ReportGenerated>>();

            // Every queue this service owns is prefixed "audit-", and the prefix is load-bearing.
            //
            // MassTransit names a consumer's queue after the message it consumes, so the default
            // formatter gives AuditConsumer<DocumentStored> a queue called "document-stored" -
            // which is exactly what Document Intelligence already called its own. Two services
            // sharing a queue name are not two subscribers on a broker; they are two competing
            // consumers on one queue, and the broker hands each message to exactly one of them.
            //
            // The failure is silent and it is a coin flip. Half the uploads get extracted and
            // never audited, the other half get audited and never extracted, and nothing anywhere
            // logs an error. It cost a full end-to-end run to find: the chain verified perfectly
            // while quietly missing the two events the audit trail exists to record - who uploaded
            // a document and who approved it.
            //
            // A prefix rather than 21 hand-written endpoint names, because the 22nd consumer would
            // have been added without one.
            bus.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(prefix: "audit", includeNamespace: false));

            if (!string.IsNullOrWhiteSpace(serviceBus))
            {
                bus.UsingAzureServiceBus((context, configurator) =>
                {
                    configurator.UseCertiflowHost(serviceBus);
                    configurator.UseConcurrencyLimit(1);
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

                // One message at a time. Each entry's id and hash derive from its predecessor, so
                // concurrent appends would fork the chain into two branches that each verify
                // internally and disagree with each other. This is the real cost of a hash chain:
                // audit write throughput is capped at one writer. Irrelevant at this scale; the
                // honest answer at larger scale is a database sequence plus periodic checkpoint
                // hashes (ADR-0003).
                configurator.UseConcurrencyLimit(1);
                configurator.ConfigureEndpoints(context);
            });
        });

        return services;
    }

}

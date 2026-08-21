using Certiflow.Messaging;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Application.Extraction;
using Certiflow.Intelligence.Infrastructure.Ai;
using Certiflow.Intelligence.Infrastructure.Messaging;
using Certiflow.Intelligence.Infrastructure.Parsing;
using Certiflow.Intelligence.Infrastructure.Persistence;
using Certiflow.Persistence;
using Certiflow.Intelligence.Infrastructure.Schemas;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Intelligence.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntelligenceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<IntelligenceDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("IntelligenceDatabase"),
                sql =>
                {
                    sql.MigrationsHistoryTable("__migrations", IntelligenceDbContext.Schema);
                    sql.EnableRetryOnFailure();
                }));

        services.Configure<AzureOpenAIOptions>(configuration.GetSection(AzureOpenAIOptions.SectionName));

        services.AddSingleton(provider =>
        {
            var endpoint = configuration[$"{AzureOpenAIOptions.SectionName}:Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");

            // Keyless. Locally this is the developer's `az login` session; in Azure it is the
            // container's managed identity. There is no key to store, rotate or leak (NFR-9).
            return new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
        });

        services.AddSingleton(provider =>
        {
            // Same two-mode construction as the intake and reporting stores: a service URI means
            // managed identity, a connection string means Azurite. This one was missed when the
            // other two were fixed, and it is the reason extraction faulted in Azure while every
            // other part of the chain worked - the worker could consume DocumentStored and then had
            // no way to fetch the document it described.
            var serviceUri = configuration["Storage:ServiceUri"];

            return string.IsNullOrWhiteSpace(serviceUri)
                ? new BlobServiceClient(configuration.GetConnectionString("Storage"))
                : new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential());
        });

        services.AddSingleton<IDocumentTextParser, PdfPigDocumentTextParser>();
        services.AddSingleton<IDocumentTypeSchemaProvider, EmbeddedSchemaProvider>();
        services.AddSingleton<IFieldExtractor, AzureOpenAIFieldExtractor>();
        services.AddScoped<ExtractionPipeline>();

        return services;
    }

    /// <summary>
    /// Wires the message broker.
    /// <para>
    /// <b>RabbitMQ locally, Azure Service Bus when deployed.</b> The consumer code is identical -
    /// that transport-independence is most of why MassTransit is here at all. The split exists
    /// because the Service Bus emulator cannot create topics and subscriptions at runtime, and
    /// MassTransit's topology does; declaring every entity by hand in an emulator config file to
    /// gain fidelity a broker abstraction already provides is the wrong trade for local
    /// development. The honest caveat: Service Bus specifics - sessions, scheduled delivery,
    /// dead-letter semantics - are therefore first exercised in Azure, not on a laptop.
    /// </para>
    /// </summary>
    public static IServiceCollection AddIntelligenceMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetConnectionString("ServiceBus");

        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<DocumentStoredConsumer>();
            bus.AddConsumer<SupplierRegisteredIntelligenceConsumer>();
            bus.AddConsumer<ProfilePublishedIntelligenceConsumer>();
            bus.SetKebabCaseEndpointNameFormatter();

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

                // Retry then dead-letter. A consumer that keeps failing must end up somewhere
                // visible rather than looping forever or dropping the message (NFR-5, NFR-6).
                configurator.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(15)));

                configurator.ConfigureEndpoints(context);
            });
        });

        services.Configure<OutboxDispatcherOptions>(configuration.GetSection(OutboxDispatcherOptions.SectionName));
        services.AddOutboxDispatcher<IntelligenceDbContext>();

        return services;
    }
}

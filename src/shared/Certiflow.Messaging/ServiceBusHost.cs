using Azure.Identity;
using MassTransit;

namespace Certiflow.Messaging;

/// <summary>
/// Points MassTransit at Azure Service Bus, with or without a connection string.
/// <para>
/// <b>This exists because the keyless path had never once been executed.</b> Locally the broker is
/// RabbitMQ, so every service's Service Bus branch was dead code until the first real deployment —
/// where all seven crashed on startup with <c>FormatException: The connection string could not be
/// parsed</c>. The templates deliberately pass a bare namespace, because the Service Bus namespace
/// has <c>disableLocalAuth</c> set and there is no connection string to pass (NFR-9), and
/// <c>Host(string)</c> wants <c>Endpoint=sb://…;SharedAccessKey=…</c>.
/// </para>
/// <para>
/// A configuration value that looks like a namespace is therefore treated as one, and authenticated
/// with <see cref="DefaultAzureCredential"/> — the same credential the worker already uses for Azure
/// OpenAI. A value that looks like a connection string is still passed through, so a developer who
/// wants to point at a real namespace with a key can.
/// </para>
/// </summary>
public static class ServiceBusHost
{
    public static void UseCertiflowHost(this IServiceBusBusFactoryConfigurator configurator, string value)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // "Endpoint=" is what every Service Bus connection string starts with. Anything else is
        // treated as a namespace, which fails loudly at connect time rather than silently doing
        // the wrong thing.
        if (value.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase))
        {
            configurator.Host(value);
            return;
        }

        var host = value.StartsWith("sb://", StringComparison.OrdinalIgnoreCase) ? value : $"sb://{value}";

        configurator.Host(new Uri(host), host =>
        {
            // The container's user-assigned identity, selected by AZURE_CLIENT_ID. No secret exists
            // anywhere in this path - which is the whole point of the identity per service.
            host.TokenCredential = new DefaultAzureCredential();
        });
    }
}

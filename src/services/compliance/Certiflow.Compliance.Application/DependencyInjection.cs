using System.Reflection;
using Certiflow.Compliance.Application.Behaviors;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Compliance.Application;

/// <summary>
/// Registers this layer's handlers, validators and pipeline.
/// <para>
/// The Application layer registers itself rather than having the host enumerate its types — the
/// host should not need to know that this service uses MediatR, and swapping it later then touches
/// one file rather than every startup path.
/// </para>
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddComplianceApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(assembly);

            // Registered before the handlers run, so nothing reaches a handler unvalidated.
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: false);
        services.AddScoped<Abstractions.ComplianceStateLoader>();

        return services;
    }
}

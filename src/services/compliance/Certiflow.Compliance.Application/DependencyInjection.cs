using Certiflow.Cqrs;
using System.Reflection;
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
        // Mediator, validators and the pipeline that runs them, in one call. This service used to
        // wire all three by hand and was the only one that got it right; the shared registration
        // exists so that is no longer something to get right.
        services.AddCertiflowMediator(Assembly.GetExecutingAssembly());

        services.AddScoped<Abstractions.ComplianceStateLoader>();

        return services;
    }
}

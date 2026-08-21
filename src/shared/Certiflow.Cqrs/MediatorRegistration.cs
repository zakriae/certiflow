using System.Reflection;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Cqrs;

public static class MediatorRegistration
{
    /// <summary>
    /// Registers MediatR, the validators, <b>and</b> the pipeline that runs them, for one
    /// Application assembly.
    /// <para>
    /// The three go together and are registered together on purpose. Splitting them is what caused
    /// the bug this replaces: four services called <c>AddMediatR</c> and
    /// <c>AddValidatorsFromAssembly</c> and stopped there, leaving validators that were resolvable,
    /// injectable, and never once invoked. There is no correct way to register two of these three.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCertiflowMediator(this IServiceCollection services, Assembly applicationAssembly)
    {
        ArgumentNullException.ThrowIfNull(applicationAssembly);

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(applicationAssembly);
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(applicationAssembly);

        return services;
    }
}

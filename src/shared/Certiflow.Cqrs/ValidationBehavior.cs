using FluentValidation;
using MediatR;

namespace Certiflow.Cqrs;

/// <summary>
/// Runs every registered <see cref="IValidator{T}"/> for a request before its handler.
/// <para>
/// One place to hang validation instead of a guard clause at the top of every handler — which is
/// most of the argument for putting a mediator in front of the use cases at all. Failures surface
/// as a <see cref="ValidationException"/> that the API layer maps to a 400 with Problem Details
/// (SRS §5.3).
/// </para>
/// <para>
/// This rejects <em>malformed requests</em> only. Illegal state is the domain's business, and the
/// aggregate throws <c>DomainRuleViolationException</c> for it, which maps to 409 instead. Both
/// layers exist deliberately (tech-stack doc §3).
/// </para>
/// <para>
/// <b>Shared because it was not.</b> This lived in Compliance, and Compliance was the only service
/// that registered it — so the validators in Registry, Intake, Verification and Reporting were
/// written, tested by nobody, and never executed. Publishing a compliance profile with an empty
/// requirement list sailed through a <c>RuleFor(c => c.Requirements).NotEmpty()</c> that was never
/// asked. A behaviour every Application layer needs belongs where every Application layer can see
/// it, and registering it belongs in one shared call rather than four remembered ones.
/// </para>
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var applicable = validators as IValidator<TRequest>[] ?? [.. validators];

        // MediatR 12's RequestHandlerDelegate takes no arguments — the cancellation-token overload
        // arrives in 13, which is past the Apache-2.0 line this project stays on.
        if (applicable.Length == 0)
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        var results = await Task.WhenAll(
            applicable.Select(validator => validator.ValidateAsync(context, cancellationToken)));

        // Every failure at once, not the first one. A caller fixing one field per round trip is a
        // caller who gives up.
        var failures = results.SelectMany(result => result.Errors).Where(failure => failure is not null).ToList();

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}

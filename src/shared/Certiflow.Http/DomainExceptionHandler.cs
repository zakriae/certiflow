using Certiflow.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Certiflow.Http;

/// <summary>
/// Maps exceptions to RFC 9457 Problem Details (SRS §5.3).
/// <para>
/// <b>The distinction this exists to preserve:</b> a malformed request and an illegal one are not
/// the same failure and must not look the same to a caller. FluentValidation rejects a request that
/// is the wrong <em>shape</em> — 400, fix your payload. A domain rule violation means the request
/// was well-formed and the system refuses it — 409, the state of the world is the problem.
/// </para>
/// <para>
/// Without this both arrive as 500, which says "we broke" about a rule working exactly as designed.
/// A reviewer told "an error occurred" when they tried to approve their own upload learns nothing;
/// a reviewer told <c>verification.task.self_review</c> with the message learns the rule.
/// </para>
/// </summary>
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        switch (exception)
        {
            case DomainRuleViolationException domain:
                // Logged at Information, not Error. A refused approval is the system working; an
                // error log for every enforced rule trains people to ignore the error log.
                HttpLog.RuleRefused(logger, domain.Rule, domain.Message);

                await WriteAsync(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    title: "The request conflicts with a domain rule.",
                    detail: domain.Message,
                    // The stable rule code, so a UI can react to a specific rule rather than
                    // string-matching an English sentence that may be translated later.
                    extensions: new Dictionary<string, object?> { ["rule"] = domain.Rule },
                    cancellationToken);

                return true;

            case IResourceNotFound notFound:
                // 404, not 500. "That id does not exist" is a normal answer to a normal question,
                // and reporting it as a server fault buries real failures in noise.
                await WriteAsync(
                    httpContext,
                    StatusCodes.Status404NotFound,
                    title: "The requested resource was not found.",
                    detail: ((Exception)notFound).Message,
                    extensions: new Dictionary<string, object?>(),
                    cancellationToken);

                return true;

            case ValidationException validation:
                await WriteAsync(
                    httpContext,
                    StatusCodes.Status400BadRequest,
                    title: "The request is not valid.",
                    detail: "One or more fields failed validation.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["errors"] = validation.Errors
                            .GroupBy(e => e.PropertyName)
                            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
                    },
                    cancellationToken);

                return true;

            default:
                // Everything else really is a 500, and is left to the default handler so the
                // failure is logged as loudly as it deserves.
                return false;
        }
    }

    private static async Task WriteAsync(
        HttpContext httpContext,
        int statusCode,
        string title,
        string detail,
        IDictionary<string, object?> extensions,
        CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = statusCode;

        var problemDetailsService = httpContext.RequestServices.GetService<IProblemDetailsService>();

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
        };

        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        if (problemDetailsService is not null)
        {
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
            });

            return;
        }

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    }
}

public static class DomainExceptionHandlerRegistration
{
    /// <summary>Registers Problem Details plus the domain-aware handler, in the required order.</summary>
    public static IServiceCollection AddCertiflowProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails();

        // Kept as a backstop for anything that reaches the outer handler without passing through
        // DomainErrorMiddleware - a domain exception thrown by a filter or by the endpoint routing
        // itself, before the middleware is on the stack.
        services.AddExceptionHandler<DomainExceptionHandler>();

        return services;
    }
}

public static partial class HttpLog
{
    [LoggerMessage(EventId = 9020, Level = LogLevel.Information, Message = "Refused by rule {Rule}: {Reason}")]
    public static partial void RuleRefused(ILogger logger, string rule, string reason);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Information, Message = "Not found: {Reason}")]
    public static partial void NotFound(ILogger logger, string reason);
}

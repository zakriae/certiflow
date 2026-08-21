using Certiflow.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Certiflow.Http;

/// <summary>
/// Turns expected domain outcomes into their proper status codes <b>before</b> ASP.NET's exception
/// handler sees them.
/// <para>
/// <b>Why a middleware and not an <c>IExceptionHandler</c>.</b> It was an IExceptionHandler, and the
/// status codes were right — but <c>ExceptionHandlerMiddleware</c> writes
/// "An unhandled exception has occurred" at <c>Error</c> level <i>before</i> it consults any
/// handler. So every 404 for a mistyped id and every 409 for a correctly enforced rule produced a
/// stack trace in the error log. That is precisely the habit this project argues against elsewhere:
/// an error log full of the system working is an error log nobody reads.
/// </para>
/// <para>
/// Registered <i>inside</i> <c>UseExceptionHandler</c>, so genuine faults still bubble out to it and
/// are logged as loudly as they deserve.
/// </para>
/// </summary>
public sealed class DomainErrorMiddleware(RequestDelegate next, ILogger<DomainErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (DomainRuleViolationException domain)
        {
            // Information, not Error. A refused approval is the system working.
            HttpLog.RuleRefused(logger, domain.Rule, domain.Message);

            await WriteAsync(context, StatusCodes.Status409Conflict,
                "The request conflicts with a domain rule.",
                domain.Message,
                // The stable rule code, so a UI can react to a specific rule rather than
                // string-matching an English sentence that may be translated later.
                new Dictionary<string, object?> { ["rule"] = domain.Rule });
        }
        catch (Exception exception) when (exception is IResourceNotFound)
        {
            HttpLog.NotFound(logger, exception.Message);

            await WriteAsync(context, StatusCodes.Status404NotFound,
                "The requested resource was not found.", exception.Message, new Dictionary<string, object?>());
        }
        catch (ValidationException validation)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                "The request is not valid.",
                "One or more fields failed validation.",
                new Dictionary<string, object?>
                {
                    ["errors"] = validation.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
                });
        }
    }

    private static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        IDictionary<string, object?> extensions)
    {
        if (context.Response.HasStarted)
        {
            // Nothing useful left to do - the body is already on the wire. Rethrowing here would
            // replace a partial response with a torn one.
            return;
        }

        context.Response.StatusCode = statusCode;

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path,
        };

        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        var service = context.RequestServices.GetService<IProblemDetailsService>();

        if (service is not null)
        {
            await service.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
            return;
        }

        await context.Response.WriteAsJsonAsync(problem);
    }
}

public static class DomainErrorMiddlewareRegistration
{
    /// <summary>
    /// Call immediately after <c>UseExceptionHandler()</c>, so expected outcomes are handled here
    /// and unexpected ones still reach the outer handler.
    /// </summary>
    public static IApplicationBuilder UseCertiflowDomainErrors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<DomainErrorMiddleware>();
    }
}

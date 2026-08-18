using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Domain;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Certiflow.Compliance.Application.Evaluation;

/// <summary>Re-derives one supplier's compliance against today and emits any transitions.</summary>
public sealed record EvaluateSupplierCommand(Guid SupplierId) : IRequest;

public sealed class EvaluateSupplierValidator : AbstractValidator<EvaluateSupplierCommand>
{
    public EvaluateSupplierValidator() => RuleFor(c => c.SupplierId).NotEmpty();
}

public sealed class EvaluateSupplierHandler(
    ISupplierComplianceRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<EvaluateSupplierCommand>
{
    public async Task Handle(EvaluateSupplierCommand command, CancellationToken cancellationToken)
    {
        var supplierId = new SupplierId(command.SupplierId);

        var state = await repository.FindAsync(supplierId, cancellationToken)
            ?? throw new SupplierComplianceStateNotFoundException(supplierId);

        state.Evaluate(clock.Today, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>What a sweep did, for logging and for the admin-facing job result.</summary>
public sealed record ExpiryWatchResult(int SuppliersEvaluated, int SuppliersFailed, DateOnly EvaluatedOn);

/// <summary>
/// The nightly Expiry Watch (FR-5.4) — re-evaluates every supplier and emits the transitions that
/// occurred. Triggered by a timer in BC7's Functions host.
/// </summary>
public sealed record RunExpiryWatchCommand : IRequest<ExpiryWatchResult>;

public sealed class RunExpiryWatchHandler(
    ISupplierComplianceRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RunExpiryWatchHandler> logger) : IRequestHandler<RunExpiryWatchCommand, ExpiryWatchResult>
{
    public async Task<ExpiryWatchResult> Handle(RunExpiryWatchCommand command, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var now = clock.UtcNow;
        var supplierIds = await repository.ListAllSupplierIdsAsync(cancellationToken);

        var evaluated = 0;
        var failed = 0;

        foreach (var supplierId in supplierIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var state = await repository.FindAsync(supplierId, cancellationToken);

                if (state is null)
                {
                    continue;
                }

                state.Evaluate(today, now);

                // Saved per supplier, not once at the end. One supplier with unexpected data must
                // not roll back the transitions of the two hundred evaluated before it — and a
                // nightly job that is all-or-nothing is a nightly job that eventually does nothing.
                await unitOfWork.SaveChangesAsync(cancellationToken);
                evaluated++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                ExpiryWatchLog.EvaluationFailed(logger, exception, supplierId.Value, today);
            }
        }

        ExpiryWatchLog.SweepCompleted(logger, evaluated, today, failed);

        return new ExpiryWatchResult(evaluated, failed, today);
    }
}

/// <summary>
/// Source-generated log methods.
/// <para>
/// The <c>[LoggerMessage]</c> generator emits a cached delegate per message, so the sweep does not
/// box its arguments or format a string on every iteration — which is what CA1848 is about, and it
/// matters here precisely because this runs once per supplier. It also fixes the event ids and the
/// message templates, so <c>SupplierId</c> stays a queryable field in Application Insights rather
/// than becoming part of a formatted string (tech-stack doc §11, Serilog/OTel).
/// </para>
/// </summary>
internal static partial class ExpiryWatchLog
{
    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Error,
        Message = "Expiry Watch failed to evaluate supplier {SupplierId} on {EvaluatedOn}")]
    public static partial void EvaluationFailed(
        ILogger logger,
        Exception exception,
        Guid supplierId,
        DateOnly evaluatedOn);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Information,
        Message = "Expiry Watch evaluated {Evaluated} supplier(s) on {EvaluatedOn}, {Failed} failed")]
    public static partial void SweepCompleted(
        ILogger logger,
        int evaluated,
        DateOnly evaluatedOn,
        int failed);
}

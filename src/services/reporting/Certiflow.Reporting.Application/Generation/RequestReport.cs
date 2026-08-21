using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;

namespace Certiflow.Reporting.Application.Generation;

/// <summary>
/// Accepts a report request and returns immediately (FR-6.4).
/// <para>
/// Nothing is rendered here. Generation calls two other services and draws a PDF; doing that on the
/// request thread would make a button click hold an HTTP connection open for as long as the slowest
/// dependency takes, and would lose the job entirely if the process recycled mid-render.
/// </para>
/// </summary>
public sealed record RequestReportCommand(Guid SupplierId, string RequestedBy) : IRequest<ReportId>;

public sealed class RequestReportValidator : AbstractValidator<RequestReportCommand>
{
    public RequestReportValidator()
    {
        RuleFor(c => c.SupplierId).NotEmpty();
        RuleFor(c => c.RequestedBy).NotEmpty().MaximumLength(200);
    }
}

public sealed class RequestReportHandler(
    IReportRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock)
    : IRequestHandler<RequestReportCommand, ReportId>
{
    public async Task<ReportId> Handle(RequestReportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var report = Report.Request(
            ReportType.SupplierComplianceCertificate,
            new SupplierId(command.SupplierId),
            command.RequestedBy,
            clock.UtcNow);

        repository.Add(report);

        // The job row and the message that will pick it up are written in one transaction by the
        // outbox, so a crash between "accepted" and "queued" is not a report that never happens.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return report.Id;
    }
}

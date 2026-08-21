using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.SharedKernel;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Certiflow.Reporting.Application.Generation;

public sealed record GenerateReportCommand(Guid ReportId) : IRequest;

/// <summary>
/// Fetches the facts, hashes them, renders them, stores the result (FR-6.1).
/// <para>
/// The order is not arbitrary. The fingerprint is computed over the snapshot <i>before</i> anything
/// is drawn, so the hash attests to the facts rather than to the bytes of a particular layout — and
/// so the PDF can print the hash of its own contents without a circular dependency.
/// </para>
/// </summary>
public sealed partial class GenerateReportHandler(
    IReportRepository repository,
    IComplianceSnapshotSource snapshots,
    IReportRenderer renderer,
    IReportBlobStore blobs,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<GenerateReportHandler> logger)
    : IRequestHandler<GenerateReportCommand>
{
    public async Task Handle(GenerateReportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reportId = new ReportId(command.ReportId);
        var report = await repository.FindAsync(reportId, cancellationToken)
            ?? throw new ReportNotFoundException(reportId);

        if (report.Status != ReportStatus.Requested)
        {
            // A redelivery of a job that already ran. Returning quietly is right: the message was
            // handled, and re-rendering would replace an immutable artefact with a second one
            // (FR-6.5).
            AlreadyGenerated(logger, reportId.Value, report.Status.ToString());
            return;
        }

        report.Start();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            var snapshot = Canonicalise(await snapshots.FetchAsync(report.Subject, cancellationToken));

            // Hash first, then render, then store. The PDF carries the hash of the facts it shows.
            var fingerprint = ReportFingerprint.Compute(snapshot);
            var generatedAt = clock.UtcNow;
            var pdf = renderer.Render(snapshot, report.Id, fingerprint, generatedAt);

            // Path carries the report id, so two reports for the same supplier on the same day
            // cannot overwrite each other - the immutability in FR-6.5 has to hold in storage too,
            // not only in the aggregate.
            var path = $"{generatedAt:yyyy}/{generatedAt:MM}/{report.Subject.Value}/{report.Id.Value}.pdf";
            var storage = await blobs.StoreAsync(pdf, path, cancellationToken);

            report.Complete(storage, fingerprint, clock.UtcNow);

            Generated(logger, reportId.Value, report.Subject.Value, pdf.Length, fingerprint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Recorded rather than rethrown. Letting this escape would dead-letter the message and
            // leave the job stuck in Generating forever, with the caller unable to tell a slow
            // report from a dead one.
            GenerationFailed(logger, exception, reportId.Value);
            report.Fail(exception.Message, clock.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Sorts obligations into a stable order before hashing.
    /// <para>
    /// Without this the fingerprint depends on whatever order the compliance service happened to
    /// return, so the same facts could produce two different hashes and verification would fail for
    /// no reason a user could act on. Mandatory first, then by document type — which is also the
    /// order a reader wants them on the page.
    /// </para>
    /// </summary>
    private static SupplierComplianceSnapshot Canonicalise(SupplierComplianceSnapshot snapshot) =>
        snapshot with
        {
            Obligations = [.. snapshot.Obligations
                .OrderByDescending(o => o.IsMandatory)
                .ThenBy(o => o.DocumentType, StringComparer.Ordinal)
                .ThenBy(o => o.RequirementId.Value)],
        };

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Information,
        Message = "Report {ReportId} is already {Status}; ignoring redelivery")]
    private static partial void AlreadyGenerated(ILogger logger, Guid reportId, string status);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Information,
        Message = "Report {ReportId} generated for supplier {SupplierId}: {Bytes} bytes, fingerprint {Fingerprint}")]
    private static partial void Generated(ILogger logger, Guid reportId, Guid supplierId, int bytes, string fingerprint);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Error,
        Message = "Report {ReportId} failed to generate")]
    private static partial void GenerationFailed(ILogger logger, Exception exception, Guid reportId);
}

using System.Security.Claims;
using Certiflow.Cqrs;
using Certiflow.Http;
using Certiflow.Persistence;
using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.Reporting.Application.Generation;
using Certiflow.Reporting.Infrastructure;
using Certiflow.Reporting.Infrastructure.Persistence;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCertiflowMediator(typeof(RequestReportCommand).Assembly);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddReportingInfrastructure(builder.Configuration);
builder.Services.AddReportingMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();
builder.Services.AddCertiflowAuthentication(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseCertiflowDomainErrors();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

// Development applies migrations on startup so a fresh clone needs no extra step. Azure does NOT:
// several Container Apps replicas racing to apply the same migration is how a deployment corrupts a
// schema, so deployment runs them once as a step before the new revision starts (NFR-19).
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    await database.MigrateSchemaAsync(logger);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "reporting" })).AllowAnonymous();

// ── Request a report (FR-6.4) ───────────────────────────────────────────────────────────────────
// 202, not 200. Generation calls two other services and renders a PDF; holding the connection open
// for that would make a button click as slow as the slowest dependency, and lose the job entirely
// if the process recycled mid-render.
app.MapPost("/api/reports/suppliers/{supplierId:guid}", async (
    Guid supplierId,
    ClaimsPrincipal user,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    var reportId = await sender.Send(
        // From the token. The requester's name is printed on the certificate and recorded in the
        // audit trail, and the name on an attestation is not something its requester should choose.
        new RequestReportCommand(supplierId, user.EmailOf()),
        cancellationToken);

    return Results.Accepted($"/api/reports/{reportId.Value}", new { reportId = reportId.Value, status = "Requested" });
});

app.MapGet("/api/reports/{reportId:guid}", async (
    Guid reportId,
    ReportingDbContext database,
    CancellationToken cancellationToken) =>
{
    var report = await database.Reports.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == new ReportId(reportId), cancellationToken);

    return report is null ? Results.NotFound() : Results.Ok(new
    {
        reportId = report.Id.Value,
        supplierId = report.Subject.Value,
        type = report.Type.ToString(),
        status = report.Status.ToString(),
        report.RequestedBy,
        report.RequestedAt,
        report.CompletedAt,
        report.VerificationHash,
        report.FailureReason,
        // A client polls this and follows the link when it appears, rather than guessing.
        downloadUrl = report.Status == ReportStatus.Completed ? $"/api/reports/{report.Id.Value}/download" : null,
    });
});

app.MapGet("/api/reports/suppliers/{supplierId:guid}", async (
    Guid supplierId,
    ReportingDbContext database,
    CancellationToken cancellationToken) =>
    Results.Ok(await database.Reports.AsNoTracking()
        .Where(r => r.Subject == new SupplierId(supplierId))
        .OrderByDescending(r => r.RequestedAt)
        .Take(50)
        .Select(r => new
        {
            reportId = r.Id.Value,
            status = r.Status.ToString(),
            r.RequestedBy,
            r.RequestedAt,
            r.CompletedAt,
            r.VerificationHash,
        })
        .ToListAsync(cancellationToken)));

// ── Download (NFR-10) ───────────────────────────────────────────────────────────────────────────
// A short-lived SAS redirect rather than the bytes, for the same reason document downloads work
// this way: the API should not become the path every page of every PDF travels through.
app.MapGet("/api/reports/{reportId:guid}/download", async (
    Guid reportId,
    ReportingDbContext database,
    IReportBlobStore blobs,
    CancellationToken cancellationToken) =>
{
    var report = await database.Reports.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == new ReportId(reportId), cancellationToken);

    if (report?.Storage is null)
    {
        return report is null
            ? Results.NotFound()
            : Results.Problem(
                $"Report {reportId} is {report.Status} and has no artefact yet.",
                statusCode: StatusCodes.Status409Conflict);
    }

    var lifetime = TimeSpan.FromMinutes(15);
    var url = await blobs.CreateReadUrlAsync(report.Storage, lifetime, cancellationToken);

    return Results.Ok(new { url, expiresInSeconds = (int)lifetime.TotalSeconds });
});

// ── Verify (FR-6.1) ─────────────────────────────────────────────────────────────────────────────
// Recomputes the fingerprint from the supplier's position *now* and compares it to what the report
// attested to. A mismatch is not tampering - it means the supplier's compliance has changed since
// the report was issued, which is exactly what someone holding a three-month-old PDF needs to know.
app.MapGet("/api/reports/{reportId:guid}/verify", async (
    Guid reportId,
    ReportingDbContext database,
    IComplianceSnapshotSource snapshots,
    CancellationToken cancellationToken) =>
{
    var report = await database.Reports.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == new ReportId(reportId), cancellationToken);

    if (report is null)
    {
        return Results.NotFound();
    }

    if (report.VerificationHash is null)
    {
        return Results.Problem(
            $"Report {reportId} is {report.Status} and never produced a fingerprint.",
            statusCode: StatusCodes.Status409Conflict);
    }

    try
    {
        var current = ReportFingerprint.Compute(await snapshots.FetchAsync(report.Subject, cancellationToken));

        return Results.Ok(new
        {
            reportId = report.Id.Value,
            issuedFingerprint = report.VerificationHash,
            currentFingerprint = current,
            stillAccurate = string.Equals(current, report.VerificationHash, StringComparison.Ordinal),
            report.CompletedAt,
            note = string.Equals(current, report.VerificationHash, StringComparison.Ordinal)
                ? "The supplier's position is unchanged since this report was issued."
                : "The supplier's position has changed since this report was issued. Request a new one.",
        });
    }
    catch (SnapshotUnavailableException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();


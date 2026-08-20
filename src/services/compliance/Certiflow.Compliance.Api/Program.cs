using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Application.Evaluation;
using Certiflow.Compliance.Application.Suppliers;
using Certiflow.Compliance.Domain;
using Certiflow.Compliance.Infrastructure;
using Certiflow.Compliance.Infrastructure.Persistence;
using Certiflow.Http;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMediatR(configuration =>
    configuration.RegisterServicesFromAssembly(typeof(RunExpiryWatchCommand).Assembly));

builder.Services.AddValidatorsFromAssembly(typeof(RunExpiryWatchCommand).Assembly);
builder.Services.AddComplianceInfrastructure(builder.Configuration);
builder.Services.AddComplianceMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
    await database.EnsureSchemaAsync(ComplianceDbContext.Schema);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "compliance" }));

// ── Portfolio dashboard (FR-5.3) ────────────────────────────────────────────────────────────────
// Counts come from the persisted status column rather than by loading every aggregate: NFR-2 gives
// this 500 ms, and the column exists precisely so the question can be answered in SQL. The column
// is only ever written by the derivation that produces it (ADR-0001).
app.MapGet("/api/dashboard", async (
    ComplianceDbContext database,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    var states = await database.SupplierCompliance.AsNoTracking().ToListAsync(cancellationToken);
    var today = clock.Today;

    var expiring = states
        .SelectMany(state => state.Obligations
            .Where(o => o.IsApplicable && o.CurrentEvidence is not null)
            .Select(o => new
            {
                supplierId = state.Id.Value,
                requirementId = o.Id.Value,
                o.DocumentType,
                expiresOn = o.CurrentEvidence!.Validity.ExpiresOn,
                daysRemaining = o.DaysRemaining(today) ?? 0,
                status = o.Status.ToString(),
            }))
        .Where(o => o.daysRemaining <= 60)
        .OrderBy(o => o.daysRemaining)
        .ToList();

    return Results.Ok(new
    {
        evaluatedOn = today,
        totals = states
            .GroupBy(s => s.OverallStatusOn(today).ToString())
            .ToDictionary(g => g.Key, g => g.Count()),
        expiringSoon = expiring,
        nonCompliant = states
            .Where(s => s.OverallStatusOn(today) == ComplianceStatus.NonCompliant)
            .Select(s => new
            {
                supplierId = s.Id.Value,
                breached = s.Obligations
                    .Where(o => o.IsApplicable && o.IsMandatory
                             && o.Status is ObligationStatus.Missing or ObligationStatus.Expired)
                    .Select(o => new { o.DocumentType, status = o.Status.ToString() }),
            }),
    });
});

// ── One supplier, obligation by obligation (FR-5.2) ─────────────────────────────────────────────
app.MapGet("/api/suppliers/{id:guid}/compliance", async (
    Guid id,
    ComplianceDbContext database,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    var state = await database.SupplierCompliance
        .AsNoTracking()
        .FirstOrDefaultAsync(s => s.Id == new SupplierId(id), cancellationToken);

    if (state is null)
    {
        return Results.NotFound();
    }

    var today = clock.Today;

    return Results.Ok(new
    {
        supplierId = state.Id.Value,
        state.CategoryId,
        state.ProfileVersion,
        // Derived on read, not read from a column. The stored status is a cache for querying; this
        // is the authority, and it cannot be stale even if no job has run (ADR-0001).
        overallStatus = state.OverallStatusOn(today).ToString(),
        state.LastEvaluatedAt,
        obligations = state.Obligations
            .Where(o => o.IsApplicable)
            .Select(o => new
            {
                requirementId = o.Id.Value,
                o.DocumentType,
                o.IsMandatory,
                status = o.StatusOn(today).ToString(),
                daysRemaining = o.DaysRemaining(today),
                evidence = o.CurrentEvidence is null ? null : new
                {
                    documentId = o.CurrentEvidence.DocumentId.Value,
                    o.CurrentEvidence.CertificateNumber,
                    o.CurrentEvidence.Issuer,
                    o.CurrentEvidence.HolderName,
                    issuedOn = o.CurrentEvidence.Validity.IssuedOn,
                    expiresOn = o.CurrentEvidence.Validity.ExpiresOn,
                    o.CurrentEvidence.ApprovedBy,
                    o.CurrentEvidence.ApprovedAt,
                },
                historyCount = o.History.Count,
            }),
    });
});

// ── The Expiry Watch, on demand (FR-5.4) ────────────────────────────────────────────────────────
// A timer trigger in BC7 calls this nightly. Exposed as an endpoint as well so the demo can show a
// certificate lapsing without waiting for midnight.
app.MapPost("/api/expiry-watch", async (ISender sender, CancellationToken cancellationToken) =>
    Results.Ok(await sender.Send(new RunExpiryWatchCommand(), cancellationToken)));

// ── Standing in for BC1 ─────────────────────────────────────────────────────────────────────────
// Supplier registration and profile publication belong to the Supplier Registry and reach this
// service as integration events. BC1 has no application layer yet, and the approved scope cut
// seeds administrative data rather than building CRUD screens for it - so the seeder posts here.
// These call the same handlers the consumers do, so the seeded path and the event path cannot
// diverge.
app.MapPost("/api/registry-sync/suppliers", async (
    RegisterSupplierRequest request,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    await sender.Send(new RegisterSupplierComplianceCommand(request.SupplierId, request.CategoryId), cancellationToken);

    return Results.Accepted($"/api/suppliers/{request.SupplierId}/compliance");
});

app.MapPost("/api/registry-sync/profiles", async (
    PublishProfileRequest request,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    await sender.Send(
        new ApplyProfileVersionCommand(request.CategoryId, request.ProfileVersion, request.Requirements),
        cancellationToken);

    return Results.NoContent();
});

app.Run();

internal sealed record RegisterSupplierRequest(Guid SupplierId, Guid CategoryId);

internal sealed record PublishProfileRequest(
    Guid CategoryId,
    int ProfileVersion,
    IReadOnlyList<RequirementDefinition> Requirements);

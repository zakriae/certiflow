using Certiflow.Cqrs;
using Certiflow.Http;
using System.Reflection;
using Certiflow.Intake.Application.Upload;
using Certiflow.Intake.Infrastructure;
using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Infrastructure.Persistence;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCertiflowMediator(typeof(UploadDocumentCommand).Assembly);

builder.Services.AddIntakeInfrastructure(builder.Configuration);
builder.Services.AddIntakeMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();
builder.Services.AddCertiflowAuthentication(builder.Configuration);

// Guardrail G4's 20 MB ceiling, enforced at the transport as well as in the aggregate. Without
// this the server buffers the whole body before the domain ever gets to reject it, which turns a
// size limit into a denial-of-service vector.
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 20 * 1024 * 1024);

var app = builder.Build();

app.UseExceptionHandler();
app.UseCertiflowDomainErrors();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

// Creates this context's own schema, not the whole database. EnsureCreated cannot be used: eight
// contexts share one database (SRS §13.1) and it is all-or-nothing per database, so whichever
// service starts second silently creates nothing. Real environments migrate as a deploy step.
// Development applies migrations on startup so a fresh clone needs no extra step. Azure does NOT:
// several Container Apps replicas racing to apply the same migration is how a deployment corrupts a
// schema, so deployment runs them once as a step before the new revision starts (NFR-19).
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<IntakeDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    await database.MigrateSchemaAsync(logger);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "intake" })).AllowAnonymous();

app.MapPost("/api/documents", async (
    [FromForm] IFormFile file,
    [FromForm] Guid supplierId,
    [FromForm] Guid requirementId,
    [FromForm] string documentType,
    [FromForm] string? uploadedBy,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    await using var content = file.OpenReadStream();

    var command = new UploadDocumentCommand(
        supplierId,
        requirementId,
        documentType,
        file.FileName,
        file.ContentType,
        content,
        // Until authentication lands this is supplied by the caller. The segregation-of-duties
        // rule in Verification compares against it, so it becomes a claim rather than a form field
        // the moment auth exists - a caller must not get to name themselves.
        uploadedBy ?? "supplier@certiflow.demo");

    var result = await sender.Send(command, cancellationToken);

    // 202, not 200: the document is stored but extraction has not run yet, and the response points
    // at the resource that will eventually carry the result (SRS §5.3).
    return Results.Accepted($"/api/documents/{result.DocumentId}", result);
})
.DisableAntiforgery()
.WithName("UploadDocument");

app.MapGet("/api/documents/{id:guid}", async (
    Guid id,
    IntakeDbContext context,
    CancellationToken cancellationToken) =>
{
    var document = await context.Documents
        .AsNoTracking()
        .FirstOrDefaultAsync(d => d.Id == new Certiflow.Intake.Domain.DocumentId(id), cancellationToken);

    return document is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            documentId = document.Id.Value,
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.PageCount,
            status = document.Status.ToString(),
            sha256 = document.Sha256?.Value,
            storage = new { document.StorageReference?.Container, document.StorageReference?.BlobPath },
            document.UploadedBy,
            document.UploadedAt,
        });
});

// ── Serving a document to a reviewer (FR-2.5, NFR-10) ───────────────────────────────────────────
// Returns a short-lived SAS URL rather than the bytes. Streaming through the API would be simpler
// and would sidestep CORS entirely, but it would also mean every page of every document flowing
// through a container that scales on queue depth - and it would quietly drop the guarantee that a
// document is only ever reachable by a link that expires.
app.MapGet("/api/documents/{id:guid}/link", async (
    Guid id,
    IntakeDbContext context,
    IDocumentBlobStore blobStore,
    CancellationToken cancellationToken) =>
{
    var document = await context.Documents
        .AsNoTracking()
        .FirstOrDefaultAsync(d => d.Id == new Certiflow.Intake.Domain.DocumentId(id), cancellationToken);

    if (document?.StorageReference is null)
    {
        return Results.NotFound();
    }

    // Fifteen minutes: long enough to read a certificate, short enough that a URL pasted into a
    // chat window is useless by the time anyone clicks it.
    var lifetime = TimeSpan.FromMinutes(15);
    var url = await blobStore.CreateReadUrlAsync(document.StorageReference, lifetime, cancellationToken);

    return Results.Ok(new { url, expiresInSeconds = (int)lifetime.TotalSeconds, document.FileName });
});

// Visible so the outbox can be inspected while the dispatcher is being built. This is a
// development aid, not part of the product's API surface.
app.MapGet("/api/_outbox", async (IntakeDbContext context, CancellationToken cancellationToken) =>
    Results.Ok(await context.Outbox
        .AsNoTracking()
        .OrderBy(m => m.OccurredAt)
        .Select(m => new { m.EventId, m.EventType, m.OccurredAt, m.PublishedAt, m.PublishAttempts })
        .ToListAsync(cancellationToken)));

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program
{
    protected Program()
    {
    }
}

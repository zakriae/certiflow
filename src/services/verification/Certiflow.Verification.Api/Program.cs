using Certiflow.Http;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Certiflow.Verification.Application.Review;
using Certiflow.Verification.Domain;
using Certiflow.Verification.Infrastructure;
using Certiflow.Verification.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMediatR(configuration =>
    configuration.RegisterServicesFromAssembly(typeof(ApproveDocumentCommand).Assembly));

builder.Services.AddValidatorsFromAssembly(typeof(ApproveDocumentCommand).Assembly);
builder.Services.AddVerificationInfrastructure(builder.Configuration);
builder.Services.AddVerificationMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<VerificationDbContext>();
    await database.EnsureSchemaAsync(VerificationDbContext.Schema);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "verification" }));

// ── The review queue (FR-4.8) ───────────────────────────────────────────────────────────────────
// Ordered by derived priority rather than by arrival. A certificate expiring in nine days matters
// more than one that arrived first, and the priority is computed from expiry proximity so the queue
// cannot drift out of order as time passes.
app.MapGet("/api/review-tasks", async (
    VerificationDbContext database,
    IClock clock,
    string? status,
    CancellationToken cancellationToken) =>
{
    var query = database.ReviewTasks.AsNoTracking();

    // Parsed before the query, not inside it: an expression tree cannot carry a named argument,
    // and EF would have no way to translate the parse to SQL even if it could.
    if (status is null)
    {
        query = query.Where(t => t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress);
    }
    else if (Enum.TryParse<ReviewTaskStatus>(status, ignoreCase: true, out var requested))
    {
        query = query.Where(t => t.Status == requested);
    }
    else
    {
        return Results.Problem(
            $"'{status}' is not a review status. Expected one of: {string.Join(", ", Enum.GetNames<ReviewTaskStatus>())}.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var tasks = await query.ToListAsync(cancellationToken);
    var today = clock.Today;

    return Results.Ok(tasks
        .Select(t => new
        {
            reviewTaskId = t.Id.Value,
            documentId = t.DocumentId.Value,
            supplierId = t.SupplierId.Value,
            t.DocumentType,
            status = t.Status.ToString(),
            raisedReason = t.RaisedReason.ToString(),
            priority = t.PriorityOn(today).ToString(),
            t.OverallConfidence,
            t.AssignedTo,
            unresolvedMandatoryFields = t.FieldReviews.Count(f => f.IsMandatory && f.AcceptedValue is null),
        })
        .OrderBy(t => t.priority)
        .ThenByDescending(t => t.unresolvedMandatoryFields));
});

// ── One task, with everything the review screen needs ───────────────────────────────────────────
// Fields carry their citation page and snippet so the UI can jump the preview to the cited page and
// highlight the text (FR-4.3) - the promoted-to-Must feature that makes grounding visible.
app.MapGet("/api/review-tasks/{id:guid}", async (
    Guid id,
    VerificationDbContext database,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    var task = await database.ReviewTasks
        .AsNoTracking()
        .FirstOrDefaultAsync(t => t.Id == new ReviewTaskId(id), cancellationToken);

    if (task is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        reviewTaskId = task.Id.Value,
        documentId = task.DocumentId.Value,
        supplierId = task.SupplierId.Value,
        extractionJobId = task.ExtractionJobId.Value,
        task.DocumentType,
        status = task.Status.ToString(),
        raisedReason = task.RaisedReason.ToString(),
        priority = task.PriorityOn(clock.Today).ToString(),
        task.OverallConfidence,
        task.UploadedBy,
        task.AssignedTo,
        canApprove = task.FieldReviews.All(f => !f.IsMandatory || f.AcceptedValue is not null),
        verdict = task.Verdict is null ? null : new
        {
            decision = task.Verdict.Decision.ToString(),
            reason = task.Verdict.Reason?.ToString(),
            task.Verdict.ReasonNote,
            task.Verdict.DecidedBy,
            task.Verdict.DecidedAt,
        },
        fields = task.FieldReviews.Select(f => new
        {
            f.FieldName,
            f.SuggestedValue,
            f.AcceptedValue,
            f.Confidence,
            f.IsMandatory,
            f.WasCorrected,
            f.ScoringNote,
            f.ReviewerNote,
            f.ResolvedBy,
            citation = f.CitationPage is null ? null : new { page = f.CitationPage, snippet = f.CitationSnippet },
        }),
    });
});

app.MapPost("/api/review-tasks/{id:guid}/fields", async (
    Guid id,
    ResolveFieldRequest request,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    await sender.Send(
        new ResolveFieldCommand(id, request.FieldName, request.AcceptedValue, request.ReviewerId, request.ReviewerNote),
        cancellationToken);

    return Results.NoContent();
});

app.MapPost("/api/review-tasks/{id:guid}/approve", async (
    Guid id,
    ApproveRequest request,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    await sender.Send(new ApproveDocumentCommand(id, request.ReviewerId), cancellationToken);

    return Results.NoContent();
});

app.MapPost("/api/review-tasks/{id:guid}/reject", async (
    Guid id,
    RejectRequest request,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    await sender.Send(
        new RejectDocumentCommand(id, request.ReviewerId, request.Reason, request.ReasonNote),
        cancellationToken);

    return Results.NoContent();
});

app.Run();

internal sealed record ResolveFieldRequest(string FieldName, string? AcceptedValue, string ReviewerId, string? ReviewerNote);

internal sealed record ApproveRequest(string ReviewerId);

internal sealed record RejectRequest(string ReviewerId, RejectionReason Reason, string? ReasonNote);

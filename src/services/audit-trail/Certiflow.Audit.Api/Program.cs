using Certiflow.Audit.Domain;
using Certiflow.Audit.Infrastructure;
using Certiflow.Audit.Infrastructure.Persistence;
using Certiflow.Http;
using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuditInfrastructure(builder.Configuration);
builder.Services.AddAuditMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    await database.EnsureSchemaAsync(AuditDbContext.Schema);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "audit" }));

// ── The ledger (FR-8.4, FR-8.5) ─────────────────────────────────────────────────────────────────
app.MapGet("/api/audit", async (
    AuditDbContext database,
    string? entityId,
    Guid? correlationId,
    string? actor,
    int? take,
    CancellationToken cancellationToken) =>
{
    var query = database.Entries.AsNoTracking().AsQueryable();

    if (!string.IsNullOrWhiteSpace(entityId)) { query = query.Where(e => e.EntityId == entityId); }
    if (correlationId is { } correlation) { query = query.Where(e => e.CorrelationId == correlation); }
    if (!string.IsNullOrWhiteSpace(actor)) { query = query.Where(e => e.Actor == actor); }

    var entries = await query
        .OrderByDescending(e => e.EntryId)
        .Take(Math.Clamp(take ?? 100, 1, 500))
        .ToListAsync(cancellationToken);

    return Results.Ok(entries.Select(e => new
    {
        entryId = e.EntryId,
        e.OccurredAt,
        e.Actor,
        e.Action,
        e.EntityType,
        e.EntityId,
        e.CorrelationId,
        // Truncated for the list view. The full payload is on the single-entry endpoint - a
        // hundred rows of raw event JSON is not a readable audit trail.
        entryHash = e.EntryHash[..12] + "…",
        previousHash = e.PreviousHash[..12] + "…",
    }));
});

app.MapGet("/api/audit/{entryId:long}", async (long entryId, AuditDbContext database, CancellationToken cancellationToken) =>
{
    var entry = await database.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == entryId, cancellationToken);

    return entry is null ? Results.NotFound() : Results.Ok(new
    {
        entryId = entry.EntryId,
        entry.OccurredAt,
        entry.Actor,
        entry.Action,
        entry.EntityType,
        entry.EntityId,
        entry.CorrelationId,
        entry.PayloadJson,
        entry.PreviousHash,
        entry.EntryHash,
        // Recomputed on read, so a tampered row is visible on its own detail page and not only in
        // a whole-chain sweep.
        hashesMatch = entry.ToDomain().RecomputeHash() == entry.EntryHash,
    });
});

// ── Verify the chain (FR-8.3) ───────────────────────────────────────────────────────────────────
app.MapGet("/api/audit/verify-chain", async (AuditDbContext database, CancellationToken cancellationToken) =>
{
    // Read in id order, which is the order the chain was built in. Verification is O(n) and has to
    // see every entry - there is no way to check a hash chain by sampling it.
    var entries = await database.Entries.AsNoTracking().OrderBy(e => e.EntryId).ToListAsync(cancellationToken);
    var result = AuditChainVerifier.Verify(entries.ConvertAll(e => e.ToDomain()));

    return Results.Ok(new
    {
        result.IsValid,
        result.EntriesVerified,
        result.FirstBrokenEntryId,
        breakKind = result.BreakKind.ToString(),
        result.Detail,
    });
});

// ── The tamper test (SRS §11.3) ─────────────────────────────────────────────────────────────────
// Edits one row directly in SQL, exactly as someone with database access would, then leaves the
// chain broken so verify-chain can name the row. Development only: an endpoint that corrupts the
// audit trail has no business existing in a deployed environment, and guarding it by anything less
// than "this build cannot do that" would be theatre.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/audit/_tamper", async (AuditDbContext database, CancellationToken cancellationToken) =>
    {
        var target = await database.Entries.AsNoTracking().OrderBy(e => e.EntryId).Skip(1).FirstOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return Results.Problem("The ledger has too few entries to tamper with.", statusCode: StatusCodes.Status409Conflict);
        }

        // Deliberately changes the actor and nothing else - the smallest possible edit, the kind
        // someone would make to shift blame. The hash is left untouched, which is precisely what
        // makes it detectable.
        var rows = await database.Database.ExecuteSqlAsync(
            $"UPDATE audit.entries SET actor = 'not-the-real-actor@example.com' WHERE entry_id = {target.EntryId}",
            cancellationToken);

        return Results.Ok(new
        {
            tamperedEntryId = target.EntryId,
            originalActor = target.Actor,
            rowsAffected = rows,
            hint = "GET /api/audit/verify-chain",
        });
    });
}

app.Run();

using Certiflow.Cqrs;
using Certiflow.Http;
using Certiflow.Persistence;
using Certiflow.SupplierRegistry.Application.Suppliers;
using Certiflow.SupplierRegistry.Domain;
using Certiflow.SupplierRegistry.Infrastructure;
using Certiflow.SupplierRegistry.Infrastructure.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCertiflowMediator(typeof(RegisterSupplierCommand).Assembly);
builder.Services.AddRegistryInfrastructure(builder.Configuration);
builder.Services.AddRegistryMessaging(builder.Configuration);
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
    var database = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    await database.MigrateSchemaAsync(logger);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "registry" })).AllowAnonymous();

// Registering a supplier with a category activates it, which is what tells Compliance to start
// tracking obligations for it. The seeder calls this; the approved scope cut builds no admin UI.
app.MapPost("/api/suppliers", async (RegisterSupplierCommand command, ISender sender, CancellationToken ct) =>
{
    var id = await sender.Send(command, ct);

    return Results.Created($"/api/suppliers/{id}", new { supplierId = id });
});

// Publishing carries the whole requirement set - including accepted issuers, which is what lets
// Document Intelligence check an issuer without ever querying this service.
app.MapPost("/api/categories/{categoryId:guid}/profile", async (
    Guid categoryId,
    PublishProfileRequest request,
    ISender sender,
    CancellationToken ct) =>
{
    var version = await sender.Send(
        new PublishProfileCommand(categoryId, request.Name, request.Requirements), ct);

    return Results.Ok(new { categoryId, publishedVersion = version });
});

app.MapGet("/api/suppliers/{id:guid}", async (Guid id, RegistryDbContext db, CancellationToken ct) =>
{
    var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == new SupplierId(id), ct);

    return supplier is null ? Results.NotFound() : Results.Ok(new
    {
        supplierId = supplier.Id.Value,
        supplier.LegalName,
        supplier.TradingName,
        registrationNumber = supplier.RegistrationNumber.Value,
        country = supplier.Country.Value,
        categoryId = supplier.CategoryId?.Value,
        status = supplier.Status.ToString(),
        contacts = supplier.Contacts.Select(c => new { c.Name, email = c.Email.Value, c.IsPrimary }),
    });
});

// Added for BC6: a compliance certificate names the category the supplier was assessed against,
// and "dddd1111-eeee-2222-ffff-333344445555" is not a name an auditor can read. The profile has
// carried the name since it was written; nothing exposed it until a report needed it.
app.MapGet("/api/categories/{categoryId:guid}", async (Guid categoryId, RegistryDbContext db, CancellationToken ct) =>
{
    var profile = await db.Profiles.AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == new CategoryId(categoryId), ct);

    return profile is null ? Results.NotFound() : Results.Ok(new
    {
        categoryId = profile.Id.Value,
        profile.Name,
        profile.PublishedVersion,
    });
});

// FR-1.6's filters. Country was missing from the projection entirely, so "filter by country" had no
// data to work with even though the aggregate has held it since BC1 was written.
app.MapGet("/api/suppliers", async (
    Guid? categoryId,
    string? country,
    string? status,
    RegistryDbContext db,
    CancellationToken ct) =>
{
    var query = db.Suppliers.AsNoTracking().AsQueryable();

    if (categoryId is { } category) { query = query.Where(s => s.CategoryId == new CategoryId(category)); }

    // Filtering happens in SQL, not after materialising every supplier: NFR-2 gives list views
    // 500 ms and "fetch everything then filter in memory" is the shape that stops meeting that
    // exactly when the data gets interesting.
    if (!string.IsNullOrWhiteSpace(country))
    {
        query = query.Where(s => s.Country.Value == country);
    }

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SupplierStatus>(status, ignoreCase: true, out var parsed))
    {
        query = query.Where(s => s.Status == parsed);
    }

    return Results.Ok(await query
        .OrderBy(s => s.LegalName)
        .Select(s => new
        {
            supplierId = s.Id.Value,
            s.LegalName,
            s.TradingName,
            registrationNumber = s.RegistrationNumber.Value,
            country = s.Country.Value,
            categoryId = s.CategoryId,
            status = s.Status.ToString(),
        })
        .ToListAsync(ct));
});

app.Run();

internal sealed record PublishProfileRequest(string Name, IReadOnlyList<RequirementInput> Requirements);

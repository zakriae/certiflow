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

builder.Services.AddMediatR(c => c.RegisterServicesFromAssembly(typeof(RegisterSupplierCommand).Assembly));
builder.Services.AddValidatorsFromAssembly(typeof(RegisterSupplierCommand).Assembly);
builder.Services.AddRegistryInfrastructure(builder.Configuration);
builder.Services.AddRegistryMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
    await database.EnsureSchemaAsync(RegistryDbContext.Schema);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "registry" }));

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

app.MapGet("/api/suppliers", async (RegistryDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Suppliers.AsNoTracking()
        .Select(s => new { supplierId = s.Id.Value, s.LegalName, categoryId = s.CategoryId, status = s.Status.ToString() })
        .ToListAsync(ct)));

app.Run();

internal sealed record PublishProfileRequest(string Name, IReadOnlyList<RequirementInput> Requirements);

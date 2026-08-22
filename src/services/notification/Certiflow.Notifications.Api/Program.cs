using System.Security.Claims;
using Certiflow.Cqrs;
using Certiflow.Http;
using Certiflow.Notifications.Application.Delivery;
using Certiflow.Notifications.Domain;
using Certiflow.Notifications.Infrastructure;
using Certiflow.Notifications.Infrastructure.Persistence;
using Certiflow.Persistence;
using Certiflow.SharedKernel;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCertiflowMediator(typeof(RaiseNotificationCommand).Assembly);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddNotificationsInfrastructure(builder.Configuration);
builder.Services.AddNotificationsMessaging(builder.Configuration);
builder.Services.AddCertiflowProblemDetails();
builder.Services.AddCertiflowAuthentication(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseCertiflowDomainErrors();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    await database.MigrateSchemaAsync(logger);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "notifications" })).AllowAnonymous();

// ── The in-app inbox (FR-7.4, FR-7.8) ───────────────────────────────────────────────────────────
// This is where mail goes when outbound email is disabled, which is the default and which FR-7.8
// insists on: a publicly reachable demo that can send real mail to any address anyone types is an
// abuse vector.
app.MapGet("/api/notifications", async (
    Guid? supplierId,
    bool? unreadOnly,
    ClaimsPrincipal user,
    NotificationsDbContext database,
    CancellationToken cancellationToken) =>
{
    var query = database.Notifications.AsNoTracking().AsQueryable();

    // NFR-8's tenant guard, and the reason supplier_id is a claim rather than a parameter: a
    // supplier user sees their own notifications and cannot ask for anyone else's, whatever they
    // put in the query string.
    if (user.SupplierIdOf() is { } ownSupplier)
    {
        query = query.Where(n => n.SupplierId == new SupplierId(ownSupplier));
    }
    else if (supplierId is { } requested)
    {
        query = query.Where(n => n.SupplierId == new SupplierId(requested));
    }

    if (unreadOnly == true)
    {
        query = query.Where(n => n.ReadAt == null);
    }

    var notifications = await query
        .OrderByDescending(n => n.RaisedAt)
        .Take(100)
        .Select(n => new
        {
            notificationId = n.Id.Value,
            supplierId = n.SupplierId.Value,
            kind = n.Kind.ToString(),
            n.Subject,
            n.Body,
            n.Recipient,
            channel = n.Channel.ToString(),
            status = n.Status.ToString(),
            n.RaisedAt,
            n.DeliveredAt,
            n.ReadAt,
            n.FailureReason,
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(notifications);
});

app.MapPost("/api/notifications/{id:guid}/read", async (
    Guid id,
    NotificationsDbContext database,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    var notification = await database.Notifications
        .FirstOrDefaultAsync(n => n.Id == new NotificationId(id), cancellationToken);

    if (notification is null)
    {
        return Results.NotFound();
    }

    notification.MarkRead(clock.UtcNow);
    await database.SaveChangesAsync(cancellationToken);

    return Results.NoContent();
});

app.Run();

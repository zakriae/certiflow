using System.Security.Claims;
using Certiflow.Gateway.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var issuer = builder.Configuration["Auth:Issuer"] ?? "http://localhost:5000";
var audience = builder.Configuration["Auth:Audience"] ?? "certiflow-api";

// The stand-in issuer. When Entra External ID lands this registration is deleted and
// Auth:Authority points at the tenant - nothing downstream changes (SRS §20 R4).
var seededIssuer = new SeededTokenIssuer(issuer, audience);
builder.Services.AddSingleton(seededIssuer);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Off. On, .NET's legacy mapping rewrites "sub" to a WS-Federation URI, "email" to another,
        // and the claims no longer match what the token actually says - so /auth/me returned nulls
        // for fields that were plainly present in the JWT. Every consumer here (services, SPA,
        // policies) reads the wire names, and Entra emits the same wire names, so mapping them to
        // 2005-era URIs is pure loss.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            // The very instance registered above, closed over rather than resolved. Building a
            // second provider here would hand the validator a different RSA key than the one that
            // signed the tokens, and every request would fail signature validation.
            IssuerSigningKeys = [seededIssuer.SigningKey],
            // Default is five minutes of leeway, which makes "your token expired" untestable
            // without waiting five extra minutes.
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "roles",
            NameClaimType = "name",
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Reviewer, policy => policy.RequireRole(Roles.Reviewer, Roles.Admin));
    options.AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));

    // Auditors read everything and write nothing (FR-8.6). Note who is *not* here: SupplierUser.
    // A supplier reaching a portfolio-wide list would be a tenant-isolation failure (NFR-8), and
    // the safe default is that they are absent from every policy they were not deliberately added
    // to.
    options.AddPolicy(Policies.ReadEverything, policy =>
        policy.RequireRole(Roles.Admin, Roles.Reviewer, Roles.Auditor));

    // Uploading is the one thing a supplier user may do, and guardrail G1 makes it the one path
    // that must never be anonymous - it is what spends tokens at Azure OpenAI.
    options.AddPolicy(Policies.Upload, policy =>
        policy.RequireRole(Roles.Admin, Roles.Reviewer, Roles.SupplierUser));
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration["Cors:SpaOrigin"] ?? "http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "gateway" }));

// ── The stand-in identity provider ──────────────────────────────────────────────────────────────
// Deleted wholesale when Entra External ID lands. Everything below this comment is the part that
// does not survive; everything above it is the part that does.

app.MapGet("/.well-known/openid-configuration", (SeededTokenIssuer tokens) =>
    Results.Ok(tokens.DiscoveryDocument()));

app.MapGet("/.well-known/jwks.json", (SeededTokenIssuer tokens) => Results.Ok(tokens.Jwks()));

app.MapGet("/auth/demo-accounts", () => Results.Ok(new
{
    // Printed on the login screen (SRS §16.2). A demo whose credentials are a secret is a demo
    // nobody can run.
    password = DemoAccounts.SharedPassword,
    accounts = DemoAccounts.All.Select(a => new { a.Email, a.DisplayName, a.Role }),
}));

app.MapPost("/auth/token", (LoginRequest request, SeededTokenIssuer tokens) =>
{
    var account = request is null ? null : DemoAccounts.Find(request.Email);

    // One message for both failures. Distinguishing "no such account" from "wrong password" tells
    // an attacker which half they got right, and there is no reason to start that habit here.
    if (account is null || request!.Password != DemoAccounts.SharedPassword)
    {
        return Results.Problem("Email or password is incorrect.", statusCode: StatusCodes.Status401Unauthorized);
    }

    var lifetime = TimeSpan.FromHours(8);

    return Results.Ok(new
    {
        accessToken = tokens.Issue(account, lifetime),
        tokenType = "Bearer",
        expiresIn = (int)lifetime.TotalSeconds,
        account.Email,
        account.DisplayName,
        account.Role,
    });
});

// ── Service-to-service tokens ───────────────────────────────────────────────────────────────────
// Available in every environment, and that is a deliberate, documented weakness of the stand-in.
//
// The comment that used to sit here said Azure would not need this because BC6 would present its
// managed identity instead. That was an intention, not an implementation, and the first deployment
// proved it: report generation failed with 404 because the endpoint was Development-only.
//
// It cannot be fixed by simply using the managed identity, either. Every service validates tokens
// against exactly one issuer - this gateway - and a managed-identity token is issued by Entra. A
// service presenting one would be rejected for a bad issuer. Making it work means the gateway
// validating an Entra token and exchanging it for one of its own, which is real work spent
// hardening a component whose entire purpose is to be deleted when Entra External ID lands (SRS
// §20 R4) - at which point the services trust Entra directly and this endpoint disappears.
//
// So: it issues a Service-role token to anyone who asks. In an environment holding only generated
// data (NFR-11), for the length of a recording session (§5.0), that is an acceptable trade. It
// would not be in a system holding anything real, and ADR-0007 records it as the one piece of the
// auth story that is demo-grade rather than production-shaped.
app.MapPost("/auth/service-token", (ServiceTokenRequest request, SeededTokenIssuer tokens) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Service))
    {
        return Results.Problem("A service name is required.", statusCode: StatusCodes.Status400BadRequest);
    }

    // An hour, not eight. A workload refreshes silently; a short life limits what a leaked token is
    // worth without inconveniencing anybody.
    var lifetime = TimeSpan.FromHours(1);

    var account = new DemoAccount(
        SubjectId: Guid.Parse("00000000-0000-0000-0000-0000000000f1"),
        Email: $"{request.Service}@certiflow.internal",
        DisplayName: $"Certiflow {request.Service}",
        Role: Roles.Service,
        SupplierId: null);

    return Results.Ok(new
    {
        accessToken = tokens.Issue(account, lifetime),
        tokenType = "Bearer",
        expiresIn = (int)lifetime.TotalSeconds,
    });
}).AllowAnonymous();

app.MapGet("/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    subject = user.FindFirstValue("sub"),
    email = user.FindFirstValue("email"),
    name = user.FindFirstValue("name"),
    roles = user.FindAll("roles").Select(c => c.Value),
    supplierId = user.FindFirstValue("supplier_id"),
})).RequireAuthorization();

// ── Routing ─────────────────────────────────────────────────────────────────────────────────────
// Every downstream route requires an authenticated caller; the per-route policies live in
// appsettings so adding a service does not mean editing this file.
app.MapReverseProxy();

app.Run();

internal sealed record LoginRequest(string Email, string Password);

internal sealed record ServiceTokenRequest(string Service);

internal static class Policies
{
    public const string Reviewer = "reviewer";

    public const string Admin = "admin";

    public const string ReadEverything = "read-everything";

    public const string Upload = "upload";
}

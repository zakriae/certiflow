using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Certiflow.Http;

/// <summary>
/// JWT validation and the role policies, identical in every service.
/// <para>
/// <b>Why each service validates the token as well as the gateway.</b> NFR-8 asks for the tenant
/// guard in both, and the reason is concrete rather than ceremonial: before this existed,
/// <c>curl http://localhost:5290/api/review-tasks</c> with no token at all returned the whole review
/// queue. Every service listened on a port, and the gateway was a suggestion. In Container Apps the
/// services are on an internal network — but "the network protects it" is exactly the assumption
/// that turns one misconfigured ingress rule into full data access.
/// </para>
/// <para>
/// The gateway's job is not to be the only check. It is to be the public front door, do the
/// coarse-grained routing check once, and hand the token onward unchanged.
/// </para>
/// </summary>
public static class CertiflowAuthentication
{
    public static IServiceCollection AddCertiflowAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var authority = configuration["Auth:Authority"] ?? "http://localhost:5000";
        var audience = configuration["Auth:Audience"] ?? "certiflow-api";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // OIDC discovery against the issuer, exactly as it will be against Entra. The
                // service fetches /.well-known/openid-configuration and the JWKS, and holds no
                // secret of its own - which is the whole point of signing with RS256 rather than
                // distributing an HMAC key to eight config files (NFR-9).
                options.Authority = authority;
                options.Audience = audience;

                // Development only: the seeded issuer is plain HTTP on localhost. In Azure the
                // authority is HTTPS and this stays false-by-default.
                options.RequireHttpsMetadata = configuration.GetValue("Auth:RequireHttpsMetadata", defaultValue: false);

                // See the gateway for why. On, .NET rewrites "sub" and "email" into WS-Federation
                // URIs and the claims stop matching what the token says.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = Claims.Roles,
                    NameClaimType = Claims.Name,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(CertiflowPolicies.Admin, policy => policy.RequireRole(CertiflowRoles.Admin));

            options.AddPolicy(CertiflowPolicies.Reviewer, policy =>
                policy.RequireRole(CertiflowRoles.Reviewer, CertiflowRoles.Admin));

            // Read-only for auditors, and note the absence of SupplierUser: a supplier reaching a
            // portfolio-wide list is a tenant-isolation failure, so they are opted in per route
            // rather than out (FR-8.6, NFR-8).
            options.AddPolicy(CertiflowPolicies.ReadEverything, policy =>
                policy.RequireRole(
                    CertiflowRoles.Admin, CertiflowRoles.Reviewer, CertiflowRoles.Auditor, CertiflowRoles.Service));

            options.AddPolicy(CertiflowPolicies.Upload, policy =>
                policy.RequireRole(CertiflowRoles.Admin, CertiflowRoles.Reviewer, CertiflowRoles.SupplierUser));

            // Deny by default. Every endpoint requires an authenticated caller unless it says
            // AllowAnonymous, so forgetting to protect a new endpoint fails closed rather than
            // publishing it.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }
}

/// <summary>The four app roles from SRS §2, as Entra External ID will emit them.</summary>
public static class CertiflowRoles
{
    public const string Admin = "Admin";

    public const string Reviewer = "Reviewer";

    public const string Auditor = "Auditor";

    public const string SupplierUser = "SupplierUser";

    /// <summary>
    /// Not a person. Carried by tokens one service uses to call another — today BC6 Reporting,
    /// which must read Compliance and Registry to assemble a certificate (ADR-0006). In Azure this
    /// is a managed identity; the seeded issuer stands in for it locally, and neither one involves
    /// a secret in a config file.
    /// </summary>
    public const string Service = "Service";
}

public static class CertiflowPolicies
{
    public const string Admin = "admin";

    public const string Reviewer = "reviewer";

    public const string ReadEverything = "read-everything";

    public const string Upload = "upload";
}

public static class Claims
{
    public const string Roles = "roles";

    public const string Name = "name";

    public const string Email = "email";

    public const string Subject = "sub";

    /// <summary>Present only on supplier-user tokens. The tenant guard reads it (NFR-8).</summary>
    public const string SupplierId = "supplier_id";
}

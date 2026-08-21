using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Certiflow.Gateway.Identity;

/// <summary>
/// Stands in for Entra External ID until the tenant exists (SRS §20 R4).
/// <para>
/// <b>It signs with RS256 and publishes a JWKS, rather than sharing an HMAC secret.</b> A symmetric
/// key would be four lines shorter and would need distributing to all eight services — a secret in
/// eight config files, which NFR-9 exists to prevent. More importantly it would make the services'
/// validation code <i>different</i> from what Entra needs, so the swap would be a rewrite instead of
/// a config change. With a JWKS, every service already does exactly what it will do in production:
/// fetch the issuer's signing keys over OIDC discovery and validate against them.
/// </para>
/// <para>
/// The key is generated at startup and lives in memory, so a gateway restart invalidates every
/// issued token. That is correct for a stand-in nobody should be depending on, and it is exactly the
/// behaviour that stops this quietly becoming the real thing.
/// </para>
/// </summary>
public sealed class SeededTokenIssuer : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);

    private readonly RsaSecurityKey _securityKey;

    private readonly string _issuer;

    private readonly string _audience;

    public SeededTokenIssuer(string issuer, string audience)
    {
        _issuer = issuer;
        _audience = audience;

        _securityKey = new RsaSecurityKey(_key)
        {
            // Stable within a process lifetime and published in the JWKS, so a validator can pick
            // the right key by kid rather than trying all of them.
            KeyId = Convert.ToHexStringLower(SHA256.HashData(_key.ExportRSAPublicKey()))[..16],
        };
    }

    /// <summary>The key a validator in this process checks signatures against.</summary>
    public SecurityKey SigningKey => _securityKey;

    public string Issue(DemoAccount account, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(account);

        var now = DateTimeOffset.UtcNow;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, account.SubjectId.ToString()),
            new(JwtRegisteredClaimNames.Email, account.Email),
            new(JwtRegisteredClaimNames.Name, account.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),

            // "roles", plural and repeatable - the shape Entra emits app roles in. Using .NET's
            // ClaimTypes.Role URI here would work locally and break on the swap.
            new("roles", account.Role),
        };

        if (account.SupplierId is { } supplierId)
        {
            // NFR-8's tenant guard travels with the token so a service can enforce it without a
            // lookup, and cannot be talked out of it by a query parameter.
            claims.Add(new Claim("supplier_id", supplierId.ToString()));
        }

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: new SigningCredentials(_securityKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>The public half, in the shape <c>/.well-known/jwks.json</c> is expected to return.</summary>
    public object Jwks()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = _securityKey.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        };
    }

    /// <summary>
    /// The discovery document. Only the fields a JWT bearer validator actually reads are present;
    /// a fuller document would imply endpoints this stand-in does not have.
    /// </summary>
    public object DiscoveryDocument() => new
    {
        issuer = _issuer,
        jwks_uri = $"{_issuer}/.well-known/jwks.json",
        token_endpoint = $"{_issuer}/auth/token",
        id_token_signing_alg_values_supported = new[] { "RS256" },
        response_types_supported = new[] { "token" },
        subject_types_supported = new[] { "public" },
    };

    public void Dispose() => _key.Dispose();
}

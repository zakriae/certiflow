using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Certiflow.Reporting.Infrastructure.Clients;

/// <summary>
/// Attaches a service identity to Reporting's outbound calls.
/// <para>
/// Reporting reads Compliance and Registry to assemble a certificate (ADR-0006), and since every
/// service now validates JWTs, those calls need to be somebody. They are not a person: nobody is
/// waiting on the other end of an async generation job, and borrowing the requester's token would
/// mean storing a user's JWT in a queued message so it could be replayed minutes later.
/// </para>
/// <para>
/// <b>In Azure this class is replaced by two lines</b> — <c>DefaultAzureCredential</c> and a scope —
/// because the managed identity issues the token and no code has to ask for one. The seeded issuer
/// stands in locally. What survives the swap is the shape: an outbound call carries a bearer token
/// for a workload identity, and no secret exists in any config file (NFR-9).
/// </para>
/// </summary>
public sealed class ServiceTokenHandler(IHttpClientFactory factory) : DelegatingHandler
{
    public const string TokenClient = "service-token";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;

    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetTokenAsync(cancellationToken));

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        // Renewed a minute early. Handing over a token that expires in flight produces a 401 that
        // looks like an authorisation bug and is really a clock.
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1))
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1))
            {
                return _token;
            }

            using var http = factory.CreateClient(TokenClient);

            var response = await http.PostAsJsonAsync(
                "/auth/service-token", new { service = "reporting" }, cancellationToken);

            response.EnsureSuccessStatusCode();

            var issued = await response.Content.ReadFromJsonAsync<IssuedToken>(cancellationToken)
                ?? throw new InvalidOperationException("The token endpoint returned an empty body.");

            _token = issued.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(issued.ExpiresIn);

            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record IssuedToken(string AccessToken, int ExpiresIn);
}

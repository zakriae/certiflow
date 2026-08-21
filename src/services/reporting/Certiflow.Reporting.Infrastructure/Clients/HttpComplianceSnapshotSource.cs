using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.SharedKernel;

namespace Certiflow.Reporting.Infrastructure.Clients;

/// <summary>
/// Assembles a report's facts by asking Compliance and Supplier Registry directly (ADR-0006).
/// <para>
/// This is the one place in Certiflow where a service calls another service synchronously, and it is
/// deliberate. Every other cross-context read in this codebase goes through an event-fed local copy
/// — BC3 keeps a registry read model for exactly that reason. A compliance certificate is different
/// in kind: it carries a verification hash and a date, and a buyer forwards it to an auditor as an
/// assertion about right now. Signing that from a copy that is a few seconds behind means attesting
/// to facts that may already be false, which is precisely the guarantee the document exists to make.
/// </para>
/// <para>
/// The cost is real and accepted: if Compliance is down, no report is produced. That is the correct
/// failure — an attestation you cannot substantiate should not be issued — and because generation is
/// asynchronous (FR-6.4) the caller sees a failed job with a reason, not a 500.
/// </para>
/// </summary>
public sealed class HttpComplianceSnapshotSource(IHttpClientFactory factory, IClock clock) : IComplianceSnapshotSource
{
    public const string ComplianceClient = "compliance";

    public const string RegistryClient = "registry";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<SupplierComplianceSnapshot> FetchAsync(SupplierId supplierId, CancellationToken cancellationToken)
    {
        var compliance = await GetAsync<ComplianceResponse>(
            ComplianceClient, $"/api/suppliers/{supplierId.Value}/compliance", cancellationToken);

        var supplier = await GetAsync<SupplierResponse>(
            RegistryClient, $"/api/suppliers/{supplierId.Value}", cancellationToken);

        // The category name is presentational, so a missing profile degrades to the id rather than
        // failing a report whose compliance facts are all present and correct.
        var categoryName = compliance.CategoryId is { } category
            ? await CategoryNameAsync(category, cancellationToken)
            : "Uncategorised";

        return new SupplierComplianceSnapshot(
            supplierId,
            supplier.LegalName,
            supplier.TradingName,
            supplier.RegistrationNumber,
            supplier.Country,
            categoryName,
            compliance.ProfileVersion,
            compliance.OverallStatus,
            // From the injected clock, not the wall clock. AsOf is part of the hashed form, so
            // reading the machine's time here would put an untestable value inside the one thing
            // the report is supposed to let you check.
            clock.Today,
            [.. compliance.Obligations.Select(Map)]);
    }

    private static ObligationLine Map(ObligationResponse obligation) =>
        new(new RequirementId(obligation.RequirementId),
            obligation.DocumentType,
            obligation.IsMandatory,
            obligation.Status,
            obligation.DaysRemaining,
            obligation.Evidence is { } evidence
                ? new EvidenceLine(
                    new DocumentId(evidence.DocumentId),
                    evidence.CertificateNumber,
                    evidence.Issuer,
                    evidence.HolderName,
                    evidence.IssuedOn,
                    evidence.ExpiresOn,
                    evidence.ApprovedBy,
                    evidence.ApprovedAt)
                : null);

    private async Task<string> CategoryNameAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        try
        {
            var category = await GetAsync<CategoryResponse>(
                RegistryClient, $"/api/categories/{categoryId}", cancellationToken);

            return category.Name;
        }
        catch (SnapshotUnavailableException)
        {
            return categoryId.ToString();
        }
    }

    private async Task<T> GetAsync<T>(string client, string path, CancellationToken cancellationToken)
    {
        using var http = factory.CreateClient(client);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Wrapped, so the generation handler records "compliance unreachable" on the job rather
            // than a socket-level message no user can act on.
            throw new SnapshotUnavailableException($"{client} did not answer {path}: {exception.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SnapshotUnavailableException($"{client} has no record at {path}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SnapshotUnavailableException($"{client} answered {(int)response.StatusCode} for {path}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new SnapshotUnavailableException($"{client} returned an empty body for {path}.");
        }
    }

    private sealed record ComplianceResponse(
        Guid SupplierId,
        Guid? CategoryId,
        int ProfileVersion,
        string OverallStatus,
        IReadOnlyList<ObligationResponse> Obligations);

    private sealed record ObligationResponse(
        Guid RequirementId,
        string DocumentType,
        bool IsMandatory,
        string Status,
        int? DaysRemaining,
        EvidenceResponse? Evidence);

    private sealed record EvidenceResponse(
        Guid DocumentId,
        string CertificateNumber,
        string Issuer,
        string HolderName,
        DateOnly IssuedOn,
        DateOnly ExpiresOn,
        string ApprovedBy,
        DateTimeOffset ApprovedAt);

    private sealed record SupplierResponse(
        string LegalName,
        string? TradingName,
        string RegistrationNumber,
        string Country);

    private sealed record CategoryResponse(Guid CategoryId, string Name, int PublishedVersion);
}

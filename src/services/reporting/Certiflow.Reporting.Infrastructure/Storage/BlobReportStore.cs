using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Certiflow.Storage;
using Microsoft.Extensions.Options;

namespace Certiflow.Reporting.Infrastructure.Storage;

public sealed class ReportStorageOptions
{
    public const string SectionName = "Storage";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Set instead of the connection string when reaching the account by identity (NFR-9).</summary>
    public string ServiceUri { get; set; } = string.Empty;

    /// <summary>
    /// A separate container from documents (SRS §13.2). Reports are immutable once written and
    /// have a different retention story from the certificates they cite; sharing a container would
    /// make "delete this supplier's uploads" quietly capable of deleting their attestations too.
    /// </summary>
    public string ReportsContainer { get; set; } = "reports";
}

public sealed class BlobReportStore : IReportBlobStore
{
    private readonly BlobContainerClient _container;

    private readonly BlobServiceClient _service;

    public BlobReportStore(IOptions<ReportStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;

        (_container, _service) = BlobAccess.CreateContainer(
            settings.ServiceUri, settings.ConnectionString, settings.ReportsContainer);
    }

    public async Task<StorageReference> StoreAsync(byte[] content, string blobPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blob = _container.GetBlobClient(blobPath);

        using var stream = new MemoryStream(content, writable: false);

        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" },
                // Refuses to overwrite. Report ids are in the path so this should never fire; if it
                // ever does, something is regenerating an artefact that is supposed to be immutable
                // and a failed upload is far better than a silently replaced attestation (FR-6.5).
                Conditions = new BlobRequestConditions { IfNoneMatch = Azure.ETag.All },
            },
            cancellationToken);

        return StorageReference.Create(_container.Name, blobPath);
    }

    public Task<string> CreateReadUrlAsync(StorageReference reference, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // The helper picks account-key or user-delegation signing depending on how the client was
        // built. The old code threw a descriptive exception when it could not sign - which was
        // honest, and still meant no report could be downloaded in Azure.
        return BlobAccess.CreateReadUrlAsync(_container, _service, reference.BlobPath, lifetime, cancellationToken);
    }
}

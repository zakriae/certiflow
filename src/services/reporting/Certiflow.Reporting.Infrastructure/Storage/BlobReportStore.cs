using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using Microsoft.Extensions.Options;

namespace Certiflow.Reporting.Infrastructure.Storage;

public sealed class ReportStorageOptions
{
    public const string SectionName = "Storage";

    public string ConnectionString { get; set; } = string.Empty;

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

    public BlobReportStore(IOptions<ReportStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _container = new BlobServiceClient(settings.ConnectionString)
            .GetBlobContainerClient(settings.ReportsContainer);

        _container.CreateIfNotExists(PublicAccessType.None);
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

        var blob = _container.GetBlobClient(reference.BlobPath);

        if (!blob.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                "The blob client cannot mint a SAS. In Azure this means the credential is a managed " +
                "identity, which needs a user-delegation key rather than an account key (NFR-9).");
        }

        var builder = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = reference.BlobPath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime),
        };

        builder.SetPermissions(BlobSasPermissions.Read);

        return Task.FromResult(blob.GenerateSasUri(builder).ToString());
    }
}

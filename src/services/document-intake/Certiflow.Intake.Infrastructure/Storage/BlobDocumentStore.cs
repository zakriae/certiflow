using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Domain;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Certiflow.Intake.Infrastructure.Storage;

public sealed class BlobStorageOptions
{
    public const string SectionName = "Storage";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Private, always. No document is ever reachable without a freshly minted, short-lived SAS
    /// (NFR-10) — a public container would put supplier insurance certificates on the open web.
    /// </summary>
    public string DocumentsContainer { get; set; } = "documents";
}

public sealed class BlobDocumentStore : IDocumentBlobStore
{
    private readonly BlobContainerClient _container;

    public BlobDocumentStore(IOptions<BlobStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        var service = new BlobServiceClient(settings.ConnectionString);

        _container = service.GetBlobContainerClient(settings.DocumentsContainer);
        _container.CreateIfNotExists(PublicAccessType.None);
    }

    public async Task<StorageReference> StoreAsync(
        Stream content,
        string blobPath,
        string contentType,
        CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(blobPath);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);

        return StorageReference.Create(_container.Name, blobPath);
    }

    public async Task<Stream> OpenReadAsync(StorageReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return await _container.GetBlobClient(reference.BlobPath).OpenReadAsync(cancellationToken: cancellationToken);
    }

    public Task<Uri> CreateReadUrlAsync(
        StorageReference reference,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var blob = _container.GetBlobClient(reference.BlobPath);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = reference.Container,
            BlobName = reference.BlobPath,
            Resource = "b",
            // A small backdated start absorbs clock skew between the app and storage; without it a
            // freshly minted link can be rejected as not-yet-valid.
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime),
        };

        builder.SetPermissions(BlobSasPermissions.Read);

        return Task.FromResult(blob.GenerateSasUri(builder));
    }
}

/// <summary>
/// Measures an uploaded file: size, content hash, and page count for PDFs.
/// <para>
/// The hash is computed by streaming rather than by buffering the file, because the 20 MB cap is
/// per document and several concurrent uploads should not each hold their content in memory.
/// </para>
/// </summary>
public sealed class DocumentInspector : IDocumentInspector
{
    public async Task<DocumentInspection> InspectAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var hash = await SHA256.HashDataAsync(content, cancellationToken);
        var size = content.CanSeek ? content.Length : 0;

        int? pageCount = null;

        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) && content.CanSeek)
        {
            content.Position = 0;

            try
            {
                using var pdf = PdfDocument.Open(content);
                pageCount = pdf.NumberOfPages;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A file that claims to be a PDF but cannot be opened is left with no page count.
                // The aggregate then accepts it on the other checks and the extraction pipeline
                // reports it as having no text layer, which is a truer description of the problem
                // than "invalid PDF" guessed at here.
                pageCount = null;
            }
        }

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        return new DocumentInspection(size, Sha256Hash.Parse(Convert.ToHexString(hash)), pageCount);
    }
}

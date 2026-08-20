using Certiflow.Intake.Domain;

namespace Certiflow.Intake.Application.Abstractions;

public interface IDocumentRepository
{
    Task<Document?> FindAsync(DocumentId documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an existing accepted document with the same content hash for the same supplier and
    /// requirement (FR-2.4). Hash rather than filename: the same certificate re-sent as
    /// <c>scan_final(2).pdf</c> is still the same certificate, and a filename check would miss it.
    /// </summary>
    Task<Document?> FindDuplicateAsync(
        SupplierId supplierId,
        RequirementId requirementId,
        Sha256Hash contentHash,
        CancellationToken cancellationToken);

    Task AddAsync(Document document, CancellationToken cancellationToken);
}

/// <summary>
/// Stores document bytes. The database holds a <see cref="StorageReference"/>, never the content
/// (FR-2.3).
/// </summary>
public interface IDocumentBlobStore
{
    Task<StorageReference> StoreAsync(
        Stream content,
        string blobPath,
        string contentType,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(StorageReference reference, CancellationToken cancellationToken);

    /// <summary>
    /// Mints a short-lived read URL (FR-2.5, NFR-10). Containers are private, so this is the only
    /// way a browser ever reaches a document, and the link dies with the SAS rather than living
    /// forever in someone's history.
    /// </summary>
    Task<Uri> CreateReadUrlAsync(
        StorageReference reference,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
}

/// <summary>What can be learned about a file before the domain will accept it.</summary>
public sealed record DocumentInspection(long SizeBytes, Sha256Hash ContentHash, int? PageCount);

/// <summary>
/// Reads size, hash and page count from the uploaded bytes.
/// <para>
/// Page count is a domain constraint (≤ 30, FR-2.2) but counting pages needs a PDF library, so the
/// measurement is a port and the <em>limit</em> stays in the aggregate.
/// </para>
/// </summary>
public interface IDocumentInspector
{
    Task<DocumentInspection> InspectAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

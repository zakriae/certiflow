using Certiflow.Verification.Domain;

namespace Certiflow.Verification.Application.Abstractions;

public interface IReviewTaskRepository
{
    Task<ReviewTask?> FindAsync(ReviewTaskId reviewTaskId, CancellationToken cancellationToken);

    /// <summary>
    /// The open task for a document, if any. Used to cancel on supersession (FR-4.9) and to keep a
    /// redelivered <c>ExtractionCompleted</c> from raising a second task for the same document.
    /// </summary>
    Task<ReviewTask?> FindOpenForDocumentAsync(DocumentId documentId, CancellationToken cancellationToken);

    Task AddAsync(ReviewTask task, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A short-lived URL for the reviewer to read the document in the browser.
/// <para>
/// Verification does not store documents — Intake does. This is a port so BC4 never reaches into
/// BC2's storage, and so the review screen can render a PDF without any container being public
/// (FR-2.5, NFR-10).
/// </para>
/// </summary>
public interface IDocumentLinkProvider
{
    Task<Uri?> CreateReadUrlAsync(DocumentId documentId, TimeSpan lifetime, CancellationToken cancellationToken);
}

namespace Certiflow.Verification.Infrastructure.Persistence;

/// <summary>
/// What Verification remembers about a document, from Intake's <c>DocumentStored</c>.
/// <para>
/// <b>This is what makes segregation of duties real.</b> The uploader travels on
/// <c>DocumentStored</c>, not on <c>ExtractionCompleted</c>, so without this table the review task
/// is raised against a placeholder and the rule — live and tested in the aggregate — compares a
/// reviewer to a constant. The rule only bites when it knows who actually uploaded the file
/// (FR-4.7).
/// </para>
/// </summary>
public sealed class DocumentRecord
{
    private DocumentRecord()
    {
        FileName = null!;
        UploadedBy = null!;
    }

    public DocumentRecord(Guid documentId, Guid supplierId, string fileName, string uploadedBy, DateTimeOffset storedAt)
    {
        DocumentId = documentId;
        SupplierId = supplierId;
        FileName = fileName;
        UploadedBy = uploadedBy;
        StoredAt = storedAt;
    }

    public Guid DocumentId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string FileName { get; private set; }

    public string UploadedBy { get; private set; }

    public DateTimeOffset StoredAt { get; private set; }
}

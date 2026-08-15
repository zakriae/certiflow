using Certiflow.Intake.Domain.Events;
using Certiflow.SharedKernel;

namespace Certiflow.Intake.Domain;

public enum DocumentStatus
{
    /// <summary>Validated and stored. The normal state.</summary>
    Accepted = 1,

    /// <summary>Failed validation. Recorded, visible to admins, never silently dropped (FR-2.7).</summary>
    Quarantined = 2,

    /// <summary>Replaced by a newer document for the same requirement. Terminal.</summary>
    Superseded = 3,
}

/// <summary>
/// An immutable stored file plus its metadata (SRS §3, §7.1).
/// <para>
/// <b>Never edited — superseded.</b> That single rule is what makes the audit trail meaningful: if a
/// document could be replaced in place, then "the reviewer approved document X" would say nothing
/// about what they actually looked at. A correction is a new <see cref="Document"/> that supersedes
/// the old one, and the old one stays exactly as it was (FR-2.6).
/// </para>
/// </summary>
public sealed class Document : AggregateRoot<DocumentId>
{
    /// <summary>Guardrail G4. Also the ceiling the API enforces before a byte is read.</summary>
    public const long MaxSizeBytes = 20 * 1024 * 1024;

    /// <summary>Guardrail G4 — bounds the token cost of one extraction.</summary>
    public const int MaxPageCount = 30;

    /// <summary>
    /// An allow-list, not a block-list. The question "is this file type dangerous?" has no reliable
    /// answer; "is this one of the three types we accept?" does.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/png", "image/jpeg" };

    private Document(
        DocumentId id,
        SupplierId supplierId,
        RequirementId? requirementId,
        string expectedDocumentType,
        string fileName,
        string contentType,
        long sizeBytes,
        Sha256Hash sha256,
        StorageReference storageReference,
        int? pageCount,
        DocumentStatus status,
        string uploadedBy,
        DateTimeOffset uploadedAt,
        DocumentId? supersedesDocumentId,
        string? quarantineReason)
        : base(id)
    {
        SupplierId = supplierId;
        RequirementId = requirementId;
        ExpectedDocumentType = expectedDocumentType;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageReference = storageReference;
        PageCount = pageCount;
        Status = status;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
        SupersedesDocumentId = supersedesDocumentId;
        QuarantineReason = quarantineReason;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private Document()
    {
        ExpectedDocumentType = null!;
        FileName = null!;
        ContentType = null!;
        Sha256 = null!;
        StorageReference = null!;
        UploadedBy = null!;
    }

    public SupplierId SupplierId { get; private set; }

    /// <summary>
    /// Null only for a document quarantined before it could be attributed to a requirement. An
    /// accepted document always has one.
    /// </summary>
    public RequirementId? RequirementId { get; private set; }

    public string ExpectedDocumentType { get; private set; }

    public string FileName { get; private set; }

    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    public Sha256Hash Sha256 { get; private set; }

    public StorageReference StorageReference { get; private set; }

    public int? PageCount { get; private set; }

    public DocumentStatus Status { get; private set; }

    public string UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    public DocumentId? SupersedesDocumentId { get; private set; }

    public string? QuarantineReason { get; private set; }

    public DocumentId? SupersededByDocumentId { get; private set; }

    /// <summary>
    /// Validates and records an accepted document, raising <see cref="DocumentStored"/> — the event
    /// that starts the extraction pipeline (FR-3.1).
    /// </summary>
    public static Document Accept(
        SupplierId supplierId,
        RequirementId requirementId,
        string expectedDocumentType,
        string fileName,
        string contentType,
        long sizeBytes,
        Sha256Hash sha256,
        StorageReference storageReference,
        int? pageCount,
        string uploadedBy,
        DateTimeOffset uploadedAt,
        DocumentId? supersedesDocumentId = null)
    {
        var safeFileName = Guard.AgainstNullOrWhiteSpace(fileName, "intake.document.file_name_required");
        Guard.AgainstTooLong(safeFileName, 260, "intake.document.file_name_too_long");

        var safeContentType = Guard.AgainstNullOrWhiteSpace(contentType, "intake.document.content_type_required");

        Guard.Require(
            AllowedContentTypes.Contains(safeContentType),
            "intake.document.content_type_not_allowed",
            $"'{safeContentType}' is not accepted. Upload a PDF, PNG or JPEG.");

        Guard.Require(
            sizeBytes > 0,
            "intake.document.empty_file",
            "The file is empty.");

        Guard.Require(
            sizeBytes <= MaxSizeBytes,
            "intake.document.too_large",
            $"The file is {sizeBytes / 1024 / 1024} MB. The limit is {MaxSizeBytes / 1024 / 1024} MB.");

        if (pageCount is { } pages)
        {
            Guard.Require(
                pages >= 1,
                "intake.document.no_pages",
                "The document has no pages.");

            Guard.Require(
                pages <= MaxPageCount,
                "intake.document.too_many_pages",
                $"The document has {pages} pages. The limit is {MaxPageCount}.");
        }

        var document = new Document(
            DocumentId.New(),
            supplierId,
            requirementId,
            Guard.AgainstNullOrWhiteSpace(expectedDocumentType, "intake.document.document_type_required"),
            safeFileName,
            safeContentType,
            sizeBytes,
            Guard.AgainstNull(sha256, "intake.document.sha256_required"),
            Guard.AgainstNull(storageReference, "intake.document.storage_reference_required"),
            pageCount,
            DocumentStatus.Accepted,
            Guard.AgainstNullOrWhiteSpace(uploadedBy, "intake.document.uploader_required"),
            uploadedAt,
            supersedesDocumentId,
            quarantineReason: null);

        document.Raise(new DocumentReceived(document.Id, supplierId, requirementId, safeFileName, uploadedBy));
        document.Raise(new DocumentStored(
            document.Id,
            supplierId,
            requirementId,
            document.ExpectedDocumentType,
            safeFileName,
            safeContentType,
            sizeBytes,
            sha256.Value,
            storageReference.Container,
            storageReference.BlobPath,
            pageCount,
            uploadedBy));

        return document;
    }

    /// <summary>
    /// Records a document that failed validation (FR-2.7).
    /// <para>
    /// A quarantined document is still a document: it is stored, it has a row, and an admin can see
    /// it. Rejecting an upload with an HTTP 400 and keeping nothing means a supplier who insists they
    /// sent it and an admin with no way to check.
    /// </para>
    /// </summary>
    public static Document Quarantine(
        SupplierId supplierId,
        RequirementId? requirementId,
        string expectedDocumentType,
        string fileName,
        string contentType,
        long sizeBytes,
        Sha256Hash sha256,
        StorageReference storageReference,
        int? pageCount,
        string uploadedBy,
        DateTimeOffset uploadedAt,
        string reason)
    {
        var safeReason = Guard.AgainstNullOrWhiteSpace(reason, "intake.document.quarantine_reason_required");

        var document = new Document(
            DocumentId.New(),
            supplierId,
            requirementId,
            Guard.AgainstNullOrWhiteSpace(expectedDocumentType, "intake.document.document_type_required"),
            Guard.AgainstNullOrWhiteSpace(fileName, "intake.document.file_name_required"),
            Guard.AgainstNullOrWhiteSpace(contentType, "intake.document.content_type_required"),
            sizeBytes,
            Guard.AgainstNull(sha256, "intake.document.sha256_required"),
            Guard.AgainstNull(storageReference, "intake.document.storage_reference_required"),
            pageCount,
            DocumentStatus.Quarantined,
            Guard.AgainstNullOrWhiteSpace(uploadedBy, "intake.document.uploader_required"),
            uploadedAt,
            supersedesDocumentId: null,
            quarantineReason: safeReason);

        document.Raise(new DocumentQuarantined(document.Id, supplierId, requirementId, safeReason));

        return document;
    }

    /// <summary>
    /// Marks this document as replaced. The only state transition an accepted document permits —
    /// and it changes nothing about the file, only records that a newer one exists.
    /// </summary>
    public void SupersededBy(DocumentId supersedingDocumentId, RequirementId requirementId)
    {
        Guard.Require(
            Status == DocumentStatus.Accepted,
            "intake.document.only_accepted_can_be_superseded",
            $"Document {Id} is {Status} and cannot be superseded.");

        Guard.Against(
            supersedingDocumentId == Id,
            "intake.document.cannot_supersede_itself",
            $"Document {Id} cannot supersede itself.");

        // SRS §7.1: a document may only supersede one in the same Requirement. Allowing a
        // cross-requirement supersede would let evidence for an insurance certificate be replaced
        // by an ISO certificate, and BC5 would faithfully record it.
        Guard.Require(
            RequirementId == requirementId,
            "intake.document.supersede_requirement_mismatch",
            $"Document {Id} belongs to requirement {RequirementId} and cannot be superseded by a document for {requirementId}.");

        Status = DocumentStatus.Superseded;
        SupersededByDocumentId = supersedingDocumentId;

        Raise(new DocumentSuperseded(Id, supersedingDocumentId, SupplierId, requirementId));
    }

    /// <summary>
    /// FR-2.4 — byte-identical resubmission for the same requirement is a duplicate.
    /// <para>
    /// The uniqueness check itself needs a repository lookup, so it lives in the Application layer.
    /// What lives here is the <em>definition</em> of a duplicate, so both sides cannot drift.
    /// </para>
    /// </summary>
    public bool IsDuplicateOf(Sha256Hash candidateHash, SupplierId supplierId, RequirementId requirementId) =>
        Status != DocumentStatus.Quarantined
        && Sha256 == candidateHash
        && SupplierId == supplierId
        && RequirementId == requirementId;
}

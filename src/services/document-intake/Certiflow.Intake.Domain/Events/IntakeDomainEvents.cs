using Certiflow.SharedKernel;

namespace Certiflow.Intake.Domain.Events;

/// <summary>
/// The upload happened. Distinct from <see cref="DocumentStored"/> because they answer different
/// questions: this one is "a supplier did something", which the audit trail and the supplier's own
/// activity view want, whereas <see cref="DocumentStored"/> is "there is a file to process".
/// </summary>
public sealed record DocumentReceived(
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId? RequirementId,
    string FileName,
    string UploadedBy) : DomainEvent;

/// <summary>
/// The bytes are durably in Blob Storage and the aggregate is committed. This is the event that
/// starts extraction (FR-3.1), and it carries a storage <em>reference</em> rather than a URL — a URL
/// in a message would outlive the message.
/// </summary>
public sealed record DocumentStored(
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId? RequirementId,
    string ExpectedDocumentType,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StorageContainer,
    string StorageBlobPath,
    int? PageCount,
    string UploadedBy) : DomainEvent;

public sealed record DocumentQuarantined(
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId? RequirementId,
    string Reason) : DomainEvent;

public sealed record DocumentSuperseded(
    DocumentId SupersededDocumentId,
    DocumentId SupersedingDocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId) : DomainEvent;

/// <summary>
/// A byte-identical resubmission was refused (FR-2.4). Recorded rather than merely returning a 409:
/// a supplier repeatedly re-uploading the same expired certificate is a compliance signal, and it is
/// invisible if the attempt leaves no trace.
/// </summary>
public sealed record DuplicateSubmissionRejected(
    DocumentId ExistingDocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    string Sha256,
    string AttemptedBy) : DomainEvent;

namespace Certiflow.Contracts;

/// <summary>
/// Published by BC2 Document Intake once the bytes are durably in Blob Storage and the
/// aggregate is committed. Consumed by BC3 Intelligence (starts extraction), BC5 Compliance
/// (moves the obligation to AwaitingReview) and BC8 Audit (SRS §12).
/// <para>
/// The event carries a <see cref="StorageContainer"/>/<see cref="StorageBlobPath"/> reference,
/// never the bytes and never a URL. BC3 mints its own short-lived SAS when it needs the file
/// (FR-2.5, NFR-10) — a URL in a message would outlive the message.
/// </para>
/// </summary>
public sealed record DocumentStored(
    Guid DocumentId,
    Guid SupplierId,
    Guid? RequirementId,
    string ExpectedDocumentType,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StorageContainer,
    string StorageBlobPath,
    int? PageCount,
    string UploadedBy,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// Validation failed. Quarantined, never silently dropped (FR-2.7) — admins can see it.
/// </summary>
public sealed record DocumentQuarantined(
    Guid DocumentId,
    Guid SupplierId,
    Guid? RequirementId,
    string Reason,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// A replacement document was accepted for the same Requirement. BC4 cancels any open review
/// task for the old document (FR-4.9); BC5 moves its evidence to history.
/// </summary>
public sealed record DocumentSuperseded(
    Guid SupersededDocumentId,
    Guid SupersedingDocumentId,
    Guid SupplierId,
    Guid RequirementId,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

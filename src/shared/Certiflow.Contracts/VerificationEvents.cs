namespace Certiflow.Contracts;

/// <summary>
/// Published by BC4 Verification. Consumed by BC7 Notification and BC8 Audit (SRS §12).
/// </summary>
public sealed record ReviewTaskRaised(
    Guid ReviewTaskId,
    Guid DocumentId,
    Guid ExtractionJobId,
    Guid SupplierId,
    Guid RequirementId,
    string RaisedReason,
    decimal OverallConfidence,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

public sealed record ReviewTaskAssigned(
    Guid ReviewTaskId,
    string AssignedTo,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// The event that makes evidence count. BC5 turns this into <c>CertificateEvidence</c> and
/// re-derives the supplier's status; BC7 emails the supplier; BC8 records it.
/// <para>
/// The approved field values travel with the event because they are the reviewer's accepted
/// values — possibly corrected, and therefore not necessarily what BC3 extracted. BC5 must
/// never reach back to the extraction to find out what was approved.
/// </para>
/// </summary>
public sealed record DocumentApproved(
    Guid ReviewTaskId,
    Guid DocumentId,
    Guid SupplierId,
    Guid RequirementId,
    string DocumentType,
    string HolderName,
    string IssuerName,
    string CertificateNumber,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string? Scope,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// Rejection always carries a reason from a controlled list, because the supplier is told it
/// verbatim (FR-4.6) and an auditor will ask to see it.
/// </summary>
public sealed record DocumentRejected(
    Guid ReviewTaskId,
    Guid DocumentId,
    Guid SupplierId,
    Guid RequirementId,
    string ReasonCode,
    string? ReasonNote,
    string RejectedBy,
    DateTimeOffset RejectedAt,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// A field the reviewer changed. Published so BC8 can render "Reviewer X corrected expiresOn
/// from A to B" (FR-8.5) and so corrections can later be exported as extraction-quality
/// training signal (FR-4.10).
/// </summary>
public sealed record FieldCorrected(
    Guid ReviewTaskId,
    Guid DocumentId,
    string FieldName,
    string? SuggestedValue,
    string? AcceptedValue,
    decimal OriginalConfidence,
    string CorrectedBy,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

public sealed record ReviewTaskCancelled(
    Guid ReviewTaskId,
    Guid DocumentId,
    string Reason,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

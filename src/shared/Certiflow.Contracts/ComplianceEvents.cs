namespace Certiflow.Contracts;

/// <summary>
/// Published by BC5 Compliance. Consumed by BC6 Reporting, BC7 Notification, BC8 Audit.
/// <para>
/// Note what this event is <em>not</em>: a command to set a status. BC5 derives status from
/// evidence and announces the transition after the fact (SRS §10.1, §19 Q12). No other context
/// may write a compliance status, and there is no API that accepts one.
/// </para>
/// </summary>
public sealed record ComplianceStatusChanged(
    Guid SupplierId,
    string PreviousStatus,
    string NewStatus,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ObligationSnapshot> Obligations,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>One obligation's state at the moment of evaluation.</summary>
public sealed record ObligationSnapshot(
    Guid RequirementId,
    string DocumentType,
    bool IsMandatory,
    string Status,
    Guid? EvidenceDocumentId,
    DateOnly? ExpiresOn,
    int? DaysRemaining);

/// <summary>
/// Emitted by the Expiry Watch (FR-5.4) when evidence enters its renewal window. BC7 turns this
/// into the T-60/T-30/T-7 reminders, deduplicated per document per window (FR-7.5).
/// </summary>
public sealed record CertificateExpiringSoon(
    Guid SupplierId,
    Guid RequirementId,
    Guid DocumentId,
    string DocumentType,
    DateOnly ExpiresOn,
    int DaysRemaining,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

public sealed record CertificateExpired(
    Guid SupplierId,
    Guid RequirementId,
    Guid DocumentId,
    string DocumentType,
    DateOnly ExpiredOn,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// Published by BC6 Reporting when an async report job finishes (FR-6.4). The blob reference
/// travels, not a SAS URL — the URL is minted when a user asks to download.
/// </summary>
public sealed record ReportGenerated(
    Guid ReportId,
    string ReportType,
    Guid? SupplierId,
    string StorageContainer,
    string StorageBlobPath,
    string VerificationHash,
    string RequestedBy,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

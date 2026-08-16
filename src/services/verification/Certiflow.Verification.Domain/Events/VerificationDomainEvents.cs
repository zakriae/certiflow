using Certiflow.SharedKernel;

namespace Certiflow.Verification.Domain.Events;

public sealed record ReviewTaskRaised(
    ReviewTaskId ReviewTaskId,
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    RaisedReason Reason,
    decimal OverallConfidence,
    ReviewPriority Priority) : DomainEvent;

public sealed record ReviewTaskAssigned(
    ReviewTaskId ReviewTaskId,
    string AssignedTo) : DomainEvent;

/// <summary>
/// A reviewer changed a value the pipeline suggested. Carries both values so the audit trail can
/// render "Reviewer X corrected expiresOn from A to B" (FR-8.5), and so corrections accumulate
/// into a labelled dataset for measuring extraction quality (FR-4.10).
/// </summary>
public sealed record FieldCorrected(
    ReviewTaskId ReviewTaskId,
    DocumentId DocumentId,
    string FieldName,
    string? SuggestedValue,
    string? AcceptedValue,
    decimal OriginalConfidence,
    string CorrectedBy) : DomainEvent;

/// <summary>
/// The event that makes evidence count. Carries the reviewer's <em>accepted</em> values, which are
/// not necessarily what the model extracted — BC5 must never reach back to the extraction to find
/// out what was approved.
/// </summary>
public sealed record DocumentApproved(
    ReviewTaskId ReviewTaskId,
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    string DocumentType,
    IReadOnlyDictionary<string, string> AcceptedValues,
    string ApprovedBy,
    DateTimeOffset ApprovedAt) : DomainEvent;

public sealed record DocumentRejected(
    ReviewTaskId ReviewTaskId,
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    RejectionReason Reason,
    string? ReasonNote,
    string RejectedBy,
    DateTimeOffset RejectedAt) : DomainEvent;

public sealed record ReviewTaskCancelled(
    ReviewTaskId ReviewTaskId,
    DocumentId DocumentId,
    string Reason) : DomainEvent;

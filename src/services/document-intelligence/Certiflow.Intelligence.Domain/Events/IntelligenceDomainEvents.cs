using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Events;

public sealed record ExtractionStarted(
    ExtractionJobId ExtractionJobId,
    DocumentId DocumentId,
    int AttemptNumber,
    string ModelUsed,
    string PromptVersion) : DomainEvent;

public sealed record ExtractionCompleted(
    ExtractionJobId ExtractionJobId,
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    Confidence OverallConfidence,
    bool AutoAcceptable,
    int TokensConsumed) : DomainEvent;

/// <summary>
/// One or more fields carried citations that are not in the document — the model produced values
/// it did not read. Raised <em>in addition to</em> <see cref="ExtractionCompleted"/>, not instead
/// of it: the job did finish, and a reviewer needs to see the result in order to reject it
/// (FR-3.4).
/// </summary>
public sealed record GroundingFailed(
    ExtractionJobId ExtractionJobId,
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    IReadOnlyList<string> UngroundedFieldNames) : DomainEvent;

public sealed record ExtractionFailed(
    ExtractionJobId ExtractionJobId,
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    string Reason,
    int AttemptCount,
    bool Abandoned) : DomainEvent;

namespace Certiflow.Contracts;

/// <summary>
/// Published by BC3 Document Intelligence when an extraction job reaches a terminal success
/// state. Consumed by BC4 Verification, BC5 Compliance and BC8 Audit (SRS §12).
/// <para>
/// <see cref="AutoAcceptable"/> is BC3 stating a fact about its own output — every mandatory
/// field met the Requirement's threshold. It is <em>not</em> an instruction to approve: only a
/// Verdict from BC4 makes evidence count (SRS §4.3, BC4↔BC5 Partnership).
/// </para>
/// </summary>
public sealed record ExtractionCompleted(
    Guid ExtractionJobId,
    Guid DocumentId,
    Guid SupplierId,
    Guid RequirementId,
    string DocumentType,
    IReadOnlyList<ExtractedFieldDescriptor> Fields,
    decimal OverallConfidence,
    bool AutoAcceptable,
    string ModelUsed,
    string PromptVersion,
    int TokensConsumed,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// One extracted field as downstream contexts see it.
/// <para>
/// <see cref="Confidence"/> is the <em>computed</em> score of SRS §8.4 — the weighted product of
/// deterministic checks — not anything the model said about itself. <see cref="GroundingResult"/>
/// is carried alongside it so a reviewer sees <em>why</em> a field scored low, and so a
/// <c>NotFoundInSource</c> field is distinguishable from one that merely failed a type check.
/// </para>
/// </summary>
public sealed record ExtractedFieldDescriptor(
    string FieldName,
    string? RawValue,
    string? TypedValue,
    decimal Confidence,
    string GroundingResult,
    bool IsMandatory,
    int? CitationPage,
    string? CitationSnippet);

/// <summary>
/// Extraction could not complete. <see cref="Abandoned"/> is true once attempts are exhausted
/// (FR-3.7) — at that point BC4 raises a manual review task and BC7 alerts an admin. Nothing is
/// ever silently lost, which is the answer to SRS §19 Q11.
/// </summary>
public sealed record ExtractionFailed(
    Guid ExtractionJobId,
    Guid DocumentId,
    Guid SupplierId,
    Guid RequirementId,
    string Reason,
    int AttemptCount,
    bool Abandoned,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// The model returned values whose citations could not be located in the source text — i.e. it
/// invented them. Raised separately from <see cref="ExtractionFailed"/> because the job did
/// succeed technically; the output is simply untrustworthy and needs a human (FR-3.4).
/// </summary>
public sealed record GroundingFailed(
    Guid ExtractionJobId,
    Guid DocumentId,
    Guid SupplierId,
    Guid RequirementId,
    IReadOnlyList<string> UngroundedFieldNames,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// Guardrail G3: the global daily token ceiling was reached. Extraction is disabled, the queue
/// is held rather than drained, and an admin is alerted. Not in the SRS §12 catalogue — added
/// because G3 promises an alert and an alert needs an event.
/// </summary>
public sealed record TokenBudgetExceeded(
    DateOnly BudgetDate,
    int TokensConsumed,
    int TokenCeiling,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

using Certiflow.Intelligence.Domain.Events;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain;

/// <summary>
/// One attempt-tracked run of the extraction pipeline over one document (SRS §8.1).
/// <para>
/// The aggregate owns two things the pipeline must not be trusted to get right on its own: the
/// <b>attempt budget</b> (guardrail G6 — three tries, then stop, so a provider outage cannot turn
/// into a retry storm billed per token) and the <b>completion bar</b> — a job cannot claim success
/// while a mandatory field has not even been attempted.
/// </para>
/// </summary>
public sealed class ExtractionJob : AggregateRoot<ExtractionJobId>
{
    /// <summary>Guardrail G6. Three attempts, then <see cref="ExtractionStatus.Abandoned"/>.</summary>
    public const int MaxAttempts = 3;

    private readonly List<ExtractedField> _fields = [];

    private ExtractionJob(
        ExtractionJobId id,
        DocumentId documentId,
        SupplierId supplierId,
        RequirementId requirementId,
        string documentType,
        Confidence autoAcceptThreshold)
        : base(id)
    {
        DocumentId = documentId;
        SupplierId = supplierId;
        RequirementId = requirementId;
        DocumentType = documentType;
        AutoAcceptThreshold = autoAcceptThreshold;
        Status = ExtractionStatus.Pending;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private ExtractionJob()
    {
        DocumentType = null!;
    }

    public DocumentId DocumentId { get; private set; }

    public SupplierId SupplierId { get; private set; }

    public RequirementId RequirementId { get; private set; }

    /// <summary>The document type the Requirement expects — i.e. what we asked the model to read.</summary>
    public string DocumentType { get; private set; }

    /// <summary>
    /// Per-requirement auto-accept bar, defaulting to 0.85 (SRS §8.4). Configurable because a
    /// public-liability insurance certificate and a safety-training record do not deserve the
    /// same scepticism.
    /// </summary>
    public Confidence AutoAcceptThreshold { get; private set; }

    public ExtractionStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public string? ModelUsed { get; private set; }

    public string? PromptVersion { get; private set; }

    public string? SchemaVersion { get; private set; }

    /// <summary>
    /// Cumulative across every attempt, successful or not (SRS §8.1). Counting only successes
    /// would under-report spend by exactly the retries that make a bad day expensive — and the
    /// daily ceiling of guardrail G3 is enforced against this number.
    /// </summary>
    public int TokensConsumed { get; private set; }

    /// <summary>
    /// Whether the text came from the PDF's own layer or from OCR (FR-3.6). Named to avoid the
    /// "Color Color" clash with the <see cref="Grounding.TextSource"/> type it holds.
    /// </summary>
    public TextSource? TextSourceUsed { get; private set; }

    public string? FailureReason { get; private set; }

    public IReadOnlyList<ExtractedField> Fields => _fields.AsReadOnly();

    /// <summary>
    /// <b>The worst-field rule (SRS §8.4).</b> A document is only as trustworthy as its weakest
    /// required field: one unlocatable expiry date makes the whole extraction untrustworthy, no
    /// matter how cleanly the other six fields scored. Averaging here would let a hallucinated
    /// date hide behind five easy fields, which is the single most dangerous thing this scoring
    /// system could do.
    /// </summary>
    public Confidence OverallConfidence =>
        _fields.Where(f => f.IsMandatory)
            .Select(f => f.Confidence)
            .DefaultIfEmpty(Confidence.Zero)
            .Min();

    /// <summary>
    /// True when every mandatory field cleared the threshold. Even then, this only means "no
    /// review task is raised automatically" — BC4 still owns the verdict, and only a verdict
    /// makes compliance evidence (SRS §4.3).
    /// </summary>
    public bool IsAutoAcceptable =>
        Status == ExtractionStatus.Completed && OverallConfidence.MeetsOrExceeds(AutoAcceptThreshold);

    public IReadOnlyList<ExtractedField> UngroundedFields =>
        [.. _fields.Where(f => f.GroundingResult == GroundingResult.NotFoundInSource)];

    public bool AttemptsExhausted => AttemptCount >= MaxAttempts;

    public static ExtractionJob Create(
        DocumentId documentId,
        SupplierId supplierId,
        RequirementId requirementId,
        string documentType,
        Confidence autoAcceptThreshold) =>
        new(
            ExtractionJobId.New(),
            documentId,
            supplierId,
            requirementId,
            Guard.AgainstNullOrWhiteSpace(documentType, "intelligence.job.document_type_required"),
            autoAcceptThreshold);

    /// <summary>
    /// Starts an attempt. Increments the attempt counter <em>before</em> any work happens, so a
    /// worker that crashes mid-attempt has still spent one of its three — otherwise a crash loop
    /// retries forever and the attempt budget protects nothing.
    /// </summary>
    public void BeginAttempt(string modelUsed, string promptVersion)
    {
        EnsureMutable();

        Guard.Require(
            !AttemptsExhausted,
            "intelligence.job.attempts_exhausted",
            $"Job {Id} has already used its {MaxAttempts} attempts.");

        Guard.Require(
            Status is ExtractionStatus.Pending or ExtractionStatus.Failed,
            "intelligence.job.attempt_already_running",
            $"Job {Id} is already in progress ({Status}).");

        ModelUsed = Guard.AgainstNullOrWhiteSpace(modelUsed, "intelligence.job.model_required");
        PromptVersion = Guard.AgainstNullOrWhiteSpace(promptVersion, "intelligence.job.prompt_version_required");
        AttemptCount++;
        Status = ExtractionStatus.Parsing;
        FailureReason = null;

        Raise(new ExtractionStarted(Id, DocumentId, AttemptCount, ModelUsed, PromptVersion));
    }

    /// <summary>Text has been obtained; records whether it came from a text layer or OCR (FR-3.6).</summary>
    public void MarkExtracting(TextSource textSource)
    {
        EnsureMutable();
        EnsureStage(ExtractionStatus.Parsing);

        TextSourceUsed = textSource;
        Status = ExtractionStatus.Extracting;
    }

    public void MarkGrounding()
    {
        EnsureMutable();
        EnsureStage(ExtractionStatus.Extracting);

        Status = ExtractionStatus.Grounding;
    }

    /// <summary>
    /// Records the scored fields and closes the job.
    /// <para>
    /// Every mandatory field the schema declares must be present — including as
    /// <see cref="ExtractedField.NotReturned"/>. "The model didn't mention it" is a legitimate
    /// outcome that a reviewer must see; a mandatory field quietly absent from the result set is
    /// not, because nothing downstream would ever notice it was missing.
    /// </para>
    /// </summary>
    public void Complete(
        DocumentTypeSchema schema,
        IReadOnlyCollection<ExtractedField> fields,
        int tokensConsumed)
    {
        EnsureMutable();
        EnsureStage(ExtractionStatus.Grounding);

        Guard.AgainstNull(schema, "intelligence.job.schema_required");
        Guard.AgainstNull(fields, "intelligence.job.fields_required");

        Guard.Require(
            string.Equals(schema.DocumentType, DocumentType, StringComparison.OrdinalIgnoreCase),
            "intelligence.job.schema_document_type_mismatch",
            $"Job {Id} expects '{DocumentType}' but was completed with a schema for '{schema.DocumentType}'.");

        var supplied = fields.Select(f => f.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unattempted = schema.MandatoryFields
            .Select(f => f.Name)
            .Where(name => !supplied.Contains(name))
            .ToList();

        Guard.Require(
            unattempted.Count == 0,
            "intelligence.job.mandatory_field_not_attempted",
            $"Cannot complete job {Id}: mandatory field(s) never attempted: {string.Join(", ", unattempted)}.");

        RecordTokens(tokensConsumed);

        _fields.Clear();
        _fields.AddRange(fields);
        SchemaVersion = schema.SchemaVersion;
        Status = ExtractionStatus.Completed;

        var ungrounded = UngroundedFields.Select(f => f.FieldName).ToList();

        if (ungrounded.Count > 0)
        {
            Raise(new GroundingFailed(Id, DocumentId, SupplierId, RequirementId, ungrounded));
        }

        Raise(new ExtractionCompleted(
            Id, DocumentId, SupplierId, RequirementId, OverallConfidence, IsAutoAcceptable, TokensConsumed));
    }

    /// <summary>
    /// An attempt failed. Tokens are still recorded — a failed call to a metered API is not free,
    /// and pretending otherwise is how a cost dashboard starts lying (guardrail G7).
    /// <para>
    /// Becomes <see cref="ExtractionStatus.Abandoned"/> once the budget is spent, which is the
    /// answer to "what happens when Azure OpenAI is down?" (SRS §19 Q11): three backed-off
    /// attempts, then a terminal state that raises an event, alerts an admin and leaves a review
    /// task for a human. Never a silent drop, never an infinite retry.
    /// </para>
    /// </summary>
    public void FailAttempt(string reason, int tokensConsumed = 0)
    {
        EnsureMutable();

        FailureReason = Guard.AgainstNullOrWhiteSpace(reason, "intelligence.job.failure_reason_required");
        RecordTokens(tokensConsumed);

        Status = AttemptsExhausted ? ExtractionStatus.Abandoned : ExtractionStatus.Failed;

        Raise(new ExtractionFailed(
            Id,
            DocumentId,
            SupplierId,
            RequirementId,
            FailureReason,
            AttemptCount,
            Abandoned: Status == ExtractionStatus.Abandoned));
    }

    private void RecordTokens(int tokensConsumed)
    {
        Guard.Require(
            tokensConsumed >= 0,
            "intelligence.job.negative_tokens",
            $"Token count cannot be negative, but was {tokensConsumed}.");

        TokensConsumed += tokensConsumed;
    }

    /// <summary>
    /// A completed or abandoned job is immutable (SRS §8.1). Re-running extraction produces a
    /// <em>new</em> job (FR-3.11) — never a mutation of the old one, because the old result may
    /// already be cited in an approved verdict and an audit trail.
    /// </summary>
    private void EnsureMutable() =>
        Guard.Against(
            Status.IsTerminal(),
            "intelligence.job.immutable",
            $"Job {Id} is {Status} and cannot be modified. Re-running extraction creates a new job.");

    private void EnsureStage(ExtractionStatus expected) =>
        Guard.Require(
            Status == expected,
            "intelligence.job.unexpected_stage",
            $"Job {Id} is {Status}, but this transition requires {expected}.");
}

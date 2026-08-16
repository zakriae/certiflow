using Certiflow.SharedKernel;
using Certiflow.Verification.Domain.Events;

// The aggregate has a `Verdict` property of type `Verdict`, so the bare name resolves to the
// property and the type's static factories become unreachable. The alias keeps both the SRS's
// vocabulary and working code.
using VerdictOf = Certiflow.Verification.Domain.Verdict;

namespace Certiflow.Verification.Domain;

/// <summary>The pipeline's suggestion for one field, as BC4 receives it from BC3.</summary>
public sealed record FieldSuggestion(
    string FieldName,
    string? SuggestedValue,
    decimal Confidence,
    bool IsMandatory,
    int? CitationPage,
    string? CitationSnippet,
    string? ScoringNote);

/// <summary>
/// A work item raised because a document could not be accepted automatically (SRS §3, §9).
/// <para>
/// <b>This aggregate is where the product stops being "the AI decided".</b> Three rules do the
/// work, and all three are enforced here rather than in the UI:
/// </para>
/// <list type="number">
/// <item><b>No approval with an unresolved mandatory field.</b> A reviewer cannot approve a
/// document by ignoring the field the pipeline was unsure about — which is otherwise exactly what
/// a rushed reviewer does.</item>
/// <item><b>Segregation of duties.</b> The person who uploaded a document cannot be the person who
/// approves it. This is the control an auditor asks about first, and a UI-only version of it is
/// worth nothing.</item>
/// <item><b>Write-once verdict.</b> Mistakes are corrected by a new submission, never by editing
/// the record.</item>
/// </list>
/// </summary>
public sealed class ReviewTask : AggregateRoot<ReviewTaskId>
{
    private readonly List<FieldReview> _fieldReviews = [];

    private ReviewTask(
        ReviewTaskId id,
        DocumentId documentId,
        ExtractionJobId extractionJobId,
        SupplierId supplierId,
        RequirementId requirementId,
        string documentType,
        string uploadedBy,
        RaisedReason reason,
        decimal overallConfidence,
        DateOnly? currentEvidenceExpiresOn)
        : base(id)
    {
        DocumentId = documentId;
        ExtractionJobId = extractionJobId;
        SupplierId = supplierId;
        RequirementId = requirementId;
        DocumentType = documentType;
        UploadedBy = uploadedBy;
        RaisedReason = reason;
        OverallConfidence = overallConfidence;
        CurrentEvidenceExpiresOn = currentEvidenceExpiresOn;
        Status = ReviewTaskStatus.Open;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private ReviewTask()
    {
        DocumentType = null!;
        UploadedBy = null!;
    }

    public DocumentId DocumentId { get; private set; }

    public ExtractionJobId ExtractionJobId { get; private set; }

    public SupplierId SupplierId { get; private set; }

    public RequirementId RequirementId { get; private set; }

    public string DocumentType { get; private set; }

    /// <summary>
    /// Who submitted the document. Held here for one reason only: it is half of the
    /// segregation-of-duties check, and that check has to be answerable without calling BC2.
    /// </summary>
    public string UploadedBy { get; private set; }

    public RaisedReason RaisedReason { get; private set; }

    public decimal OverallConfidence { get; private set; }

    /// <summary>
    /// When the evidence currently in force for this requirement expires, if any. Drives
    /// <see cref="PriorityOn"/>; null when the supplier has never held evidence for it.
    /// </summary>
    public DateOnly? CurrentEvidenceExpiresOn { get; private set; }

    public ReviewTaskStatus Status { get; private set; }

    public string? AssignedTo { get; private set; }

    public Verdict? Verdict { get; private set; }

    public string? CancellationReason { get; private set; }

    public IReadOnlyList<FieldReview> FieldReviews => _fieldReviews.AsReadOnly();

    public IEnumerable<FieldReview> UnresolvedMandatoryFields =>
        _fieldReviews.Where(f => f.IsMandatory && !f.IsResolved);

    /// <summary>
    /// FR-4.5 — the gate on approval. Note it is not "every field": optional fields a reviewer
    /// chose not to fill in must not block a decision.
    /// </summary>
    public bool CanApprove => Status is ReviewTaskStatus.Open or ReviewTaskStatus.InProgress
        && !UnresolvedMandatoryFields.Any();

    /// <summary>
    /// Derived from expiry proximity, never stored (FR-4.8). A queue whose priorities are written
    /// once and left is wrong by the next morning.
    /// </summary>
    public ReviewPriority PriorityOn(DateOnly today)
    {
        if (CurrentEvidenceExpiresOn is not { } expiresOn)
        {
            // Nothing in force means the supplier is already missing this requirement. Urgent, but
            // not more urgent than evidence actively lapsing — that one has a deadline.
            return ReviewPriority.High;
        }

        var daysRemaining = expiresOn.DayNumber - today.DayNumber;

        return daysRemaining switch
        {
            < 0 => ReviewPriority.Critical,
            <= 7 => ReviewPriority.Critical,
            <= 30 => ReviewPriority.High,
            <= 90 => ReviewPriority.Normal,
            _ => ReviewPriority.Low,
        };
    }

    /// <summary>
    /// Named <c>RaiseFor</c> rather than <c>Raise</c> to stay clear of
    /// <see cref="AggregateRoot{TId}.Raise(IDomainEvent)"/>, which this method also calls.
    /// </summary>
    public static ReviewTask RaiseFor(
        DocumentId documentId,
        ExtractionJobId extractionJobId,
        SupplierId supplierId,
        RequirementId requirementId,
        string documentType,
        string uploadedBy,
        RaisedReason reason,
        decimal overallConfidence,
        IReadOnlyCollection<FieldSuggestion> suggestions,
        DateOnly today,
        DateOnly? currentEvidenceExpiresOn = null)
    {
        Guard.AgainstNull(suggestions, "verification.task.suggestions_required");

        Guard.Require(
            suggestions.Count > 0,
            "verification.task.no_fields",
            "A review task with no fields to review would give a reviewer nothing to do.");

        var duplicates = suggestions
            .GroupBy(s => s.FieldName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Guard.Require(
            duplicates.Count == 0,
            "verification.task.duplicate_field",
            $"Duplicate fields in review task: {string.Join(", ", duplicates)}.");

        var task = new ReviewTask(
            ReviewTaskId.New(),
            documentId,
            extractionJobId,
            supplierId,
            requirementId,
            Guard.AgainstNullOrWhiteSpace(documentType, "verification.task.document_type_required"),
            Guard.AgainstNullOrWhiteSpace(uploadedBy, "verification.task.uploader_required"),
            reason,
            Guard.AgainstOutOfRange(overallConfidence, 0m, 1m, "verification.task.confidence_out_of_range"),
            currentEvidenceExpiresOn);

        foreach (var suggestion in suggestions)
        {
            task._fieldReviews.Add(new FieldReview(
                suggestion.FieldName,
                suggestion.SuggestedValue,
                suggestion.Confidence,
                suggestion.IsMandatory,
                suggestion.CitationPage,
                suggestion.CitationSnippet,
                suggestion.ScoringNote));
        }

        task.Raise(new ReviewTaskRaised(
            task.Id,
            documentId,
            supplierId,
            requirementId,
            reason,
            overallConfidence,
            task.PriorityOn(today)));

        return task;
    }

    public void AssignTo(string reviewerId)
    {
        EnsureOpen();

        AssignedTo = Guard.AgainstNullOrWhiteSpace(reviewerId, "verification.task.reviewer_required");
        Status = ReviewTaskStatus.InProgress;

        Raise(new ReviewTaskAssigned(Id, AssignedTo));
    }

    /// <summary>
    /// Records a reviewer's decision on one field — accepting the suggestion or correcting it
    /// (FR-4.4). Passing the suggested value back unchanged is the "accept" path; there is no
    /// separate method, because a reviewer confirming a value and a reviewer changing one are the
    /// same act of taking responsibility for it.
    /// </summary>
    public void ResolveField(
        string fieldName,
        string? acceptedValue,
        string reviewerId,
        DateTimeOffset now,
        string? reviewerNote = null)
    {
        EnsureOpen();

        var field = _fieldReviews.SingleOrDefault(f =>
            string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainRuleViolationException(
                "verification.task.unknown_field",
                $"Review task {Id} has no field '{fieldName}'.");

        var wasCorrected = field.Resolve(acceptedValue, reviewerId, reviewerNote, now);

        Status = ReviewTaskStatus.InProgress;

        if (wasCorrected)
        {
            Raise(new FieldCorrected(
                Id, DocumentId, field.FieldName, field.SuggestedValue, field.AcceptedValue, field.Confidence, reviewerId));
        }
    }

    /// <summary>
    /// Approves the document, which is what turns an extraction into compliance evidence.
    /// <para>
    /// Both gates are checked here, server-side, not in the UI (FR-4.7). A reviewer who has not
    /// resolved a mandatory field cannot approve, and nobody can approve their own upload.
    /// </para>
    /// </summary>
    public void Approve(string reviewerId, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureNotSelfReview(reviewerId);

        var unresolved = UnresolvedMandatoryFields.Select(f => f.FieldName).ToList();

        Guard.Require(
            unresolved.Count == 0,
            "verification.task.mandatory_fields_unresolved",
            $"Cannot approve: mandatory field(s) still unresolved: {string.Join(", ", unresolved)}.");

        Verdict = VerdictOf.Approve(reviewerId, now);
        Status = ReviewTaskStatus.Completed;

        var acceptedValues = _fieldReviews
            .Where(f => f.AcceptedValue is not null)
            .ToDictionary(f => f.FieldName, f => f.AcceptedValue!, StringComparer.OrdinalIgnoreCase);

        Raise(new DocumentApproved(
            Id, DocumentId, SupplierId, RequirementId, DocumentType, acceptedValues, reviewerId, now));
    }

    /// <summary>
    /// Rejects the document. Unlike approval this does <em>not</em> require every mandatory field to
    /// be resolved — a reviewer rejects precisely because something is wrong, and making them fill
    /// in the fields of a document they are throwing out would be absurd.
    /// </summary>
    public void Reject(string reviewerId, RejectionReason reason, string? reasonNote, DateTimeOffset now)
    {
        EnsureOpen();
        EnsureNotSelfReview(reviewerId);

        var verdict = VerdictOf.Reject(reason, reasonNote, reviewerId, now);
        Verdict = verdict;
        Status = ReviewTaskStatus.Completed;

        Raise(new DocumentRejected(
            Id, DocumentId, SupplierId, RequirementId, reason, verdict.ReasonNote, reviewerId, now));
    }

    /// <summary>
    /// Cancels an open task because the document was superseded or withdrawn (FR-4.9). A cancelled
    /// task can never receive a verdict — otherwise a reviewer could approve a document that has
    /// already been replaced, and BC5 would record evidence from a stale file.
    /// </summary>
    public void Cancel(string reason)
    {
        Guard.Against(
            Status == ReviewTaskStatus.Completed,
            "verification.task.already_decided",
            $"Review task {Id} already has a verdict and cannot be cancelled.");

        if (Status == ReviewTaskStatus.Cancelled)
        {
            // Idempotent: DocumentSuperseded may be delivered more than once (NFR-5).
            return;
        }

        CancellationReason = Guard.AgainstNullOrWhiteSpace(reason, "verification.task.cancellation_reason_required");
        Status = ReviewTaskStatus.Cancelled;

        Raise(new ReviewTaskCancelled(Id, DocumentId, CancellationReason));
    }

    private void EnsureOpen()
    {
        Guard.Against(
            Status == ReviewTaskStatus.Completed,
            "verification.task.already_decided",
            $"Review task {Id} already has a verdict. A mistake is corrected by a new submission, not by editing history.");

        Guard.Against(
            Status == ReviewTaskStatus.Cancelled,
            "verification.task.cancelled",
            $"Review task {Id} was cancelled ({CancellationReason}) and cannot receive a verdict.");
    }

    /// <summary>
    /// Segregation of duties (SRS §9.1). Compared case-insensitively because identity providers are
    /// not consistent about the casing of a UPN, and a control that can be defeated by capitalising
    /// an email address is not a control.
    /// </summary>
    private void EnsureNotSelfReview(string reviewerId)
    {
        Guard.AgainstNullOrWhiteSpace(reviewerId, "verification.task.reviewer_required");

        Guard.Against(
            string.Equals(reviewerId.Trim(), UploadedBy.Trim(), StringComparison.OrdinalIgnoreCase),
            "verification.task.segregation_of_duties",
            $"'{reviewerId}' uploaded this document and cannot also decide it.");
    }
}

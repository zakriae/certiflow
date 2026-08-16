using Certiflow.SharedKernel;

namespace Certiflow.Verification.Domain;

/// <summary>
/// One field on the review screen: what the pipeline suggested, how much it trusted itself, where
/// it read it, and what the reviewer decided (SRS §9.1).
/// <para>
/// <see cref="SuggestedValue"/> is never overwritten. The corrected value lands in
/// <see cref="AcceptedValue"/> alongside it, so the before/after pair survives — which is what
/// FR-4.4 requires for the audit trail and what FR-4.10 later exports as extraction-quality
/// training signal.
/// </para>
/// </summary>
public sealed class FieldReview : Entity<string>
{
    internal FieldReview(
        string fieldName,
        string? suggestedValue,
        decimal confidence,
        bool isMandatory,
        int? citationPage,
        string? citationSnippet,
        string? scoringNote)
        : base(Guard.AgainstNullOrWhiteSpace(fieldName, "verification.field.name_required"))
    {
        SuggestedValue = suggestedValue;
        Confidence = Guard.AgainstOutOfRange(confidence, 0m, 1m, "verification.field.confidence_out_of_range");
        IsMandatory = isMandatory;
        CitationPage = citationPage;
        CitationSnippet = citationSnippet;
        ScoringNote = scoringNote;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private FieldReview() : base(string.Empty)
    {
    }

    public string FieldName => Id;

    public string? SuggestedValue { get; private set; }

    /// <summary>The computed confidence from BC3, carried across the context boundary as a number.</summary>
    public decimal Confidence { get; private set; }

    public bool IsMandatory { get; private set; }

    /// <summary>Drives FR-4.3: clicking the field scrolls the preview to this page.</summary>
    public int? CitationPage { get; private set; }

    public string? CitationSnippet { get; private set; }

    /// <summary>Why the score is what it is, so an amber field explains itself to the reviewer.</summary>
    public string? ScoringNote { get; private set; }

    public string? AcceptedValue { get; private set; }

    public bool WasCorrected { get; private set; }

    public string? ReviewerNote { get; private set; }

    public string? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>
    /// A field is resolved once a reviewer has actively said what its value is. Deliberately not
    /// "has a non-null suggested value": the point of the review step is that a human took
    /// responsibility for the number, and inferring that from the pipeline's own output would make
    /// the whole step decorative.
    /// </summary>
    public bool IsResolved => ResolvedAt is not null;

    /// <summary>
    /// Accepts the suggested value unchanged, or a corrected one. Both go through the same method
    /// because from the domain's point of view they are the same act — a reviewer stating the
    /// value — and only the audit trail cares which it was.
    /// </summary>
    internal bool Resolve(string? acceptedValue, string reviewerId, string? reviewerNote, DateTimeOffset now)
    {
        Guard.AgainstNullOrWhiteSpace(reviewerId, "verification.field.reviewer_required");

        Guard.Require(
            !string.IsNullOrWhiteSpace(acceptedValue),
            "verification.field.accepted_value_required",
            $"Field '{FieldName}' cannot be resolved to an empty value. Reject the document instead.");

        AcceptedValue = acceptedValue!.Trim();
        WasCorrected = !string.Equals(AcceptedValue, SuggestedValue?.Trim(), StringComparison.Ordinal);
        ReviewerNote = string.IsNullOrWhiteSpace(reviewerNote) ? null : reviewerNote.Trim();
        ResolvedBy = reviewerId;
        ResolvedAt = now;

        return WasCorrected;
    }
}

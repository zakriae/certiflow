using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain;

/// <summary>
/// One typed value pulled from a document, with its computed confidence and its provenance
/// (SRS §3, §8.1).
/// <para>
/// The invariant that matters: <b>a field either carries a located citation, or it is marked as
/// ungrounded.</b> There is no third state — no value that "came from the document" without
/// anyone being able to say where. That is what makes every number on the review screen
/// traceable, and it is enforced in the constructor rather than trusted to callers.
/// </para>
/// </summary>
public sealed record ExtractedField
{
    public ExtractedField(
        string fieldName,
        string? rawValue,
        string? typedValue,
        bool isMandatory,
        Confidence confidence,
        GroundingResult groundingResult,
        Citation? citation,
        IReadOnlyList<SignalOutcome> signals,
        string? note = null)
    {
        FieldName = Guard.AgainstNullOrWhiteSpace(fieldName, "intelligence.field.name_required");

        Guard.Require(
            groundingResult != GroundingResult.Verified || citation is { IsLocated: true },
            "intelligence.field.verified_without_citation",
            $"Field '{fieldName}' is marked Verified but carries no located citation.");

        Guard.Require(
            groundingResult == GroundingResult.Verified || confidence == Confidence.Zero,
            "intelligence.field.ungrounded_with_confidence",
            $"Field '{fieldName}' is not grounded, so its confidence must be zero but was {confidence}.");

        RawValue = rawValue;
        TypedValue = typedValue;
        IsMandatory = isMandatory;
        Confidence = confidence;
        GroundingResult = groundingResult;
        Citation = citation;
        Signals = signals ?? [];
        Note = note;
    }

    public string FieldName { get; }

    /// <summary>The value exactly as the model returned it. Never overwritten — a reviewer sees the claim.</summary>
    public string? RawValue { get; }

    /// <summary>
    /// The value after parsing to its declared type, in a round-trippable form (ISO-8601 for
    /// dates). Null when the raw value did not parse, which is itself a scored signal.
    /// </summary>
    public string? TypedValue { get; }

    public bool IsMandatory { get; }

    public Confidence Confidence { get; }

    public GroundingResult GroundingResult { get; }

    public Citation? Citation { get; }

    /// <summary>
    /// The individual checks behind <see cref="Confidence"/>. Kept so the review screen can
    /// explain an amber score, and so the audit trail records why a document was auto-accepted.
    /// </summary>
    public IReadOnlyList<SignalOutcome> Signals { get; }

    public string? Note { get; }

    public bool IsGrounded => GroundingResult == GroundingResult.Verified;

    /// <summary>
    /// Whether this field alone clears the requirement's auto-accept bar. A document needs
    /// <em>every</em> mandatory field to clear it (SRS §8.4 worst-field rule).
    /// </summary>
    public bool MeetsThreshold(Confidence threshold) => Confidence.MeetsOrExceeds(threshold);

    /// <summary>
    /// A field the model was asked for and did not return. Distinct from an ungrounded field: the
    /// model declining to answer is honest behaviour, whereas an unlocatable citation is not.
    /// </summary>
    public static ExtractedField NotReturned(string fieldName, bool isMandatory) =>
        new(
            fieldName,
            rawValue: null,
            typedValue: null,
            isMandatory,
            Confidence.Zero,
            GroundingResult.NotAttempted,
            citation: null,
            signals: [],
            note: "The model returned no value for this field.");
}

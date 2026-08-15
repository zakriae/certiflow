using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Scoring;

/// <summary>The deterministic checks that make up a field's confidence (SRS §8.4).</summary>
public enum ConfidenceSignal
{
    /// <summary>The cited snippet was located in the source text. Dominant, and a veto.</summary>
    Grounding = 1,

    /// <summary>The value parses to its declared type — date, enum, or a known regex.</summary>
    TypeValidity = 2,

    /// <summary>Expiry after issue, plausible validity span, standard matching the requirement.</summary>
    CrossFieldConsistency = 3,

    /// <summary>Holder name matches the supplier; issuer is in the accepted list.</summary>
    EntityMatch = 4,

    /// <summary>An optional second pass at temperature 0 returned the same value (FR-3.10).</summary>
    ModelAgreement = 5,
}

/// <summary>
/// One check's result. <paramref name="Score"/> is in [0,1]: fuzzy checks such as name matching
/// return a real number, boolean checks return 0 or 1.
/// </summary>
public sealed record SignalOutcome
{
    private SignalOutcome(ConfidenceSignal signal, decimal score, string? detail)
    {
        Signal = signal;
        Score = Guard.AgainstOutOfRange(score, 0m, 1m, "intelligence.signal.score_out_of_range");
        Detail = detail;
    }

    public ConfidenceSignal Signal { get; }

    public decimal Score { get; }

    /// <summary>
    /// Why the check landed where it did, in words a reviewer can read. This is what turns an
    /// amber 0.62 from an unexplained number into "holder name 'Meridian Logistic' did not match
    /// supplier 'Meridian Logistics SARL'" — the difference between a reviewer trusting the tool
    /// and a reviewer re-reading all forty pages.
    /// </summary>
    public string? Detail { get; }

    public static SignalOutcome Pass(ConfidenceSignal signal, string? detail = null) =>
        new(signal, 1m, detail);

    public static SignalOutcome Fail(ConfidenceSignal signal, string? detail = null) =>
        new(signal, 0m, detail);

    public static SignalOutcome Partial(ConfidenceSignal signal, decimal score, string? detail = null) =>
        new(signal, score, detail);
}

/// <summary>
/// A field's confidence together with the checks that produced it. Carried through to the review
/// screen and the audit trail, because a score nobody can explain is a score nobody will act on.
/// </summary>
public sealed record ConfidenceBreakdown(
    Confidence Confidence,
    IReadOnlyList<SignalOutcome> Signals,
    bool GroundingVetoed,
    string? VetoReason);

/// <summary>
/// <b>Computes confidence from deterministic checks instead of asking the model (SRS §8.4).</b>
/// <para>
/// The weights below are the SRS table. Two properties of this design matter more than the exact
/// numbers, and both are what to say on camera:
/// </para>
/// <list type="number">
/// <item>
/// <b>Grounding is a veto, not a weight.</b> A field whose citation cannot be found scores zero —
/// not 0.60. Losing 40% would still let a fabricated value clear a 0.85 threshold if the other
/// checks happened to pass, and "the date is well-formed and consistent" is worthless when the
/// date is not in the document.
/// </item>
/// <item>
/// <b>Unevaluated signals are excluded and the remaining weights renormalised</b>, rather than
/// counted as failures. Model agreement (FR-3.10) is optional; if it never runs, a perfect field
/// should still score 1.00, not 0.95. A check that silently costs you 5% for not existing is a
/// bug that only shows up as unexplained review volume.
/// </item>
/// </list>
/// </summary>
public static class ConfidenceCalculator
{
    /// <summary>The SRS §8.4 weights. Sum to 1.00 when every signal is evaluated.</summary>
    public static readonly IReadOnlyDictionary<ConfidenceSignal, decimal> Weights =
        new Dictionary<ConfidenceSignal, decimal>
        {
            [ConfidenceSignal.Grounding] = 0.40m,
            [ConfidenceSignal.TypeValidity] = 0.20m,
            [ConfidenceSignal.CrossFieldConsistency] = 0.20m,
            [ConfidenceSignal.EntityMatch] = 0.15m,
            [ConfidenceSignal.ModelAgreement] = 0.05m,
        };

    public static ConfidenceBreakdown Compute(IReadOnlyCollection<SignalOutcome> outcomes)
    {
        Guard.AgainstNull(outcomes, "intelligence.confidence.outcomes_required");

        var duplicates = outcomes.GroupBy(o => o.Signal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Guard.Require(
            duplicates.Count == 0,
            "intelligence.confidence.duplicate_signal",
            $"Each signal may be reported once; duplicated: {string.Join(", ", duplicates)}.");

        var signals = outcomes.OrderBy(o => o.Signal).ToList();
        var grounding = signals.SingleOrDefault(o => o.Signal == ConfidenceSignal.Grounding);

        // No grounding check at all is treated exactly like a failed one. Grounding is not
        // optional (FR-3.4): a field nobody verified is a field nobody should trust.
        if (grounding is null)
        {
            return new ConfidenceBreakdown(
                Confidence.Zero,
                signals,
                GroundingVetoed: true,
                VetoReason: "No grounding check was performed for this field.");
        }

        if (grounding.Score == 0m)
        {
            return new ConfidenceBreakdown(
                Confidence.Zero,
                signals,
                GroundingVetoed: true,
                VetoReason: grounding.Detail ?? "The cited text could not be located in the source document.");
        }

        var totalWeight = signals.Sum(o => Weights[o.Signal]);
        var weightedScore = signals.Sum(o => Weights[o.Signal] * o.Score);

        return new ConfidenceBreakdown(
            Confidence.FromScore(weightedScore / totalWeight),
            signals,
            GroundingVetoed: false,
            VetoReason: null);
    }
}

using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Scoring;

/// <summary>
/// A computed score in [0,1] (SRS §3).
/// <para>
/// <b>Never the model's self-assessment.</b> LLM self-reported confidence is not calibrated — a
/// model will say "0.95" about a value it invented — so it must never gate a compliance decision
/// (SRS §8.4, §19 Q4). Every instance of this type comes out of
/// <see cref="ConfidenceCalculator"/>, which is why the constructor is private.
/// </para>
/// </summary>
public readonly record struct Confidence : IComparable<Confidence>
{
    public static readonly Confidence Zero = new(0m);

    public static readonly Confidence Certain = new(1m);

    private Confidence(decimal value) => Value = value;

    public decimal Value { get; }

    /// <summary>
    /// Rounds toward zero, deliberately. A raw score of 0.8499 becoming 0.85 would auto-accept a
    /// field the checks did not actually clear; in a compliance product the rounding error has to
    /// fall on the side of asking a human.
    /// </summary>
    public static Confidence FromScore(decimal rawScore)
    {
        Guard.AgainstOutOfRange(rawScore, 0m, 1m, "intelligence.confidence.out_of_range");

        return new Confidence(Math.Round(rawScore, 2, MidpointRounding.ToZero));
    }

    /// <summary>
    /// Reconstitutes a stored score. Used by EF materialisation and by seeded demo extractions
    /// (guardrail G8); not a way to invent a confidence in application code.
    /// </summary>
    public static Confidence FromPersistedValue(decimal value)
    {
        Guard.AgainstOutOfRange(value, 0m, 1m, "intelligence.confidence.out_of_range");

        return new Confidence(value);
    }

    public bool MeetsOrExceeds(Confidence threshold) => Value >= threshold.Value;

    public int CompareTo(Confidence other) => Value.CompareTo(other.Value);

    public static bool operator <(Confidence left, Confidence right) => left.Value < right.Value;

    public static bool operator >(Confidence left, Confidence right) => left.Value > right.Value;

    public static bool operator <=(Confidence left, Confidence right) => left.Value <= right.Value;

    public static bool operator >=(Confidence left, Confidence right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

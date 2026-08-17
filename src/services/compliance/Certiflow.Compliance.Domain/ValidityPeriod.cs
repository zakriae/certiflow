using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Domain;

/// <summary>
/// Issued-on → Expires-on: the interval during which evidence satisfies a Requirement (SRS §3).
/// <para>
/// A <c>record</c>, so two periods with the same dates are equal without an <c>Equals</c>
/// override — exactly what a value object wants. Mapped as an EF owned type into the parent
/// table; it has no identity and no table of its own.
/// </para>
/// </summary>
public sealed record ValidityPeriod
{
    /// <summary>
    /// Five years. A certificate claiming a longer span is far more likely to be a
    /// mis-extracted date than a real 10-year certificate, and the same bound feeds the
    /// cross-field consistency check in BC3 (SRS §8.4).
    /// </summary>
    public const int MaxPlausibleYears = 5;

    public ValidityPeriod(DateOnly issuedOn, DateOnly expiresOn)
    {
        Guard.Require(
            expiresOn > issuedOn,
            "compliance.validity.expires_after_issued",
            $"Expiry {expiresOn:O} must be after issue date {issuedOn:O}.");

        Guard.Require(
            expiresOn <= issuedOn.AddYears(MaxPlausibleYears),
            "compliance.validity.implausible_span",
            $"Validity period {issuedOn:O} → {expiresOn:O} exceeds {MaxPlausibleYears} years.");

        IssuedOn = issuedOn;
        ExpiresOn = expiresOn;
    }

    public DateOnly IssuedOn { get; }

    public DateOnly ExpiresOn { get; }

    /// <summary>Inclusive at both ends: a certificate is valid on the day it expires.</summary>
    public bool IsValidOn(DateOnly date) => date >= IssuedOn && date <= ExpiresOn;

    /// <summary>Negative once expired, which keeps the at-risk comparisons in §10.1 total.</summary>
    public int DaysRemaining(DateOnly from) => ExpiresOn.DayNumber - from.DayNumber;
}

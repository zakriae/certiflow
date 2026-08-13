using Certiflow.SharedKernel;

namespace Certiflow.SupplierRegistry.Domain;

public readonly record struct SupplierId(Guid Value)
{
    public static SupplierId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct CategoryId(Guid Value)
{
    public static CategoryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct RequirementId(Guid Value)
{
    public static RequirementId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An email address, validated only as far as is useful.
/// <para>
/// Deliberately not RFC 5322: the full grammar accepts addresses no mail server will deliver to, and
/// the only real test of an address is sending to it. This checks the shape that catches typing
/// mistakes and stops there.
/// </para>
/// </summary>
public sealed record EmailAddress
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static EmailAddress Parse(string value)
    {
        var trimmed = Guard.AgainstNullOrWhiteSpace(value, "registry.email.required");
        Guard.AgainstTooLong(trimmed, 254, "registry.email.too_long");

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);

        Guard.Require(
            at > 0 && at == trimmed.LastIndexOf('@') && at < trimmed.Length - 1,
            "registry.email.invalid",
            $"'{trimmed}' is not a valid email address.");

        var domain = trimmed[(at + 1)..];

        Guard.Require(
            domain.Contains('.', StringComparison.Ordinal) && !domain.StartsWith('.') && !domain.EndsWith('.'),
            "registry.email.invalid_domain",
            $"'{trimmed}' does not have a valid domain.");

        Guard.Against(
            trimmed.Contains(' ', StringComparison.Ordinal),
            "registry.email.invalid",
            $"'{trimmed}' is not a valid email address.");

        return new EmailAddress(trimmed.ToLowerInvariant());
    }

    public override string ToString() => Value;
}

/// <summary>
/// An ISO 3166-1 alpha-2 country code.
/// <para>
/// Uppercased and length-checked rather than validated against a list of countries: the list changes,
/// and a supplier in a country this build has never heard of should not be un-registerable.
/// </para>
/// </summary>
public sealed record CountryCode
{
    private CountryCode(string value) => Value = value;

    public string Value { get; }

    public static CountryCode Parse(string value)
    {
        var trimmed = Guard.AgainstNullOrWhiteSpace(value, "registry.country.required");

        Guard.Require(
            trimmed.Length == 2 && trimmed.All(char.IsAsciiLetter),
            "registry.country.invalid",
            $"'{trimmed}' is not an ISO 3166-1 alpha-2 country code.");

        return new CountryCode(trimmed.ToUpperInvariant());
    }

    public override string ToString() => Value;
}

/// <summary>
/// A company registration number — SIRET in France, Companies House number in the UK, and so on.
/// <para>
/// Format is not validated per country. It is normalised aggressively instead, because the same
/// number gets typed as <c>"FR 123 456 789"</c> and <c>"fr123456789"</c>, and the uniqueness rule of
/// SRS §6.1 ("unique per country") is worthless if those two count as different companies.
/// </para>
/// </summary>
public sealed record RegistrationNumber
{
    private RegistrationNumber(string value, string normalized)
    {
        Value = value;
        Normalized = normalized;
    }

    /// <summary>As entered, for display.</summary>
    public string Value { get; }

    /// <summary>Uppercased with all separators removed. This is what uniqueness compares.</summary>
    public string Normalized { get; }

    public static RegistrationNumber Parse(string value)
    {
        var trimmed = Guard.AgainstNullOrWhiteSpace(value, "registry.registration_number.required");
        Guard.AgainstTooLong(trimmed, 64, "registry.registration_number.too_long");

        var normalized = new string([.. trimmed.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

        Guard.Require(
            normalized.Length >= 4,
            "registry.registration_number.too_short",
            $"'{trimmed}' does not look like a registration number.");

        return new RegistrationNumber(trimmed, normalized);
    }

    /// <summary>Equality is on the normalised form — see the class remarks.</summary>
    public bool IsSameAs(RegistrationNumber other) =>
        string.Equals(Normalized, other.Normalized, StringComparison.Ordinal);

    public override string ToString() => Value;
}

/// <summary>
/// The kind of document a Requirement asks for, e.g. <c>ISO 9001</c>.
/// <para>
/// A value object because it crosses three context boundaries and keys the extraction schema in BC3
/// (FR-3.9). A typo in a bare string here would silently mean "no schema for this document type".
/// </para>
/// </summary>
public sealed record DocumentType
{
    private DocumentType(string value) => Value = value;

    public string Value { get; }

    public static DocumentType Parse(string value)
    {
        var trimmed = Guard.AgainstNullOrWhiteSpace(value, "registry.document_type.required");
        Guard.AgainstTooLong(trimmed, 100, "registry.document_type.too_long");

        return new DocumentType(trimmed);
    }

    public bool IsSameAs(DocumentType other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value;
}

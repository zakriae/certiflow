namespace Certiflow.Intelligence.Domain.Schemas;

/// <summary>
/// The baseline field names of SRS §8.3, as constants rather than string literals scattered
/// across scoring, mapping and the review UI. Cross-field consistency rules have to name fields
/// somehow, and a typo in a magic string here would silently disable a check.
/// </summary>
public static class CertificateFieldNames
{
    public const string HolderName = "holderName";

    public const string IssuerName = "issuerName";

    public const string CertificateNumber = "certificateNumber";

    public const string IssuedOn = "issuedOn";

    public const string ExpiresOn = "expiresOn";

    public const string Scope = "scope";

    public const string Standard = "standard";
}

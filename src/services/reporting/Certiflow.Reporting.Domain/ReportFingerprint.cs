using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Certiflow.Reporting.Domain;

/// <summary>
/// The verification hash printed on every report (FR-6.1).
/// <para>
/// SHA-256 over a length-prefixed canonical form of the facts, so anyone holding the PDF can ask
/// this service to recompute it and find out whether the numbers on the page are the numbers the
/// system issued.
/// </para>
/// <para>
/// The technique is the same one <c>AuditEntry</c> uses and it is deliberately duplicated rather
/// than shared. Length prefixes are what stop two different reports hashing identically: plain
/// concatenation lets an issuer of <c>"AFNOR|X"</c> with holder <c>"Y"</c> produce the same bytes as
/// issuer <c>"AFNOR"</c> with holder <c>"X|Y"</c>. But hashing rules are domain rules — the day
/// Reporting needs to include a field Audit does not have, a shared helper becomes a negotiation
/// between two bounded contexts over a method signature (ADR-0004). Thirty lines is the cheaper
/// side of that trade.
/// </para>
/// </summary>
public static class ReportFingerprint
{
    public static string Compute(SupplierComplianceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var canonical = new StringBuilder();

        Append(canonical, snapshot.SupplierId.ToString());
        Append(canonical, snapshot.LegalName);
        Append(canonical, snapshot.TradingName ?? string.Empty);
        Append(canonical, snapshot.RegistrationNumber);
        Append(canonical, snapshot.CountryCode);
        Append(canonical, snapshot.CategoryName);
        Append(canonical, snapshot.ProfileVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, snapshot.OverallStatus);
        Append(canonical, snapshot.AsOf.ToString("O", CultureInfo.InvariantCulture));

        // Obligation order is part of the hashed form, so the caller must present them in a stable
        // order. The generation handler sorts before it hashes; without that, two runs over the same
        // data would disagree purely because SQL returned the rows differently.
        Append(canonical, snapshot.Obligations.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var obligation in snapshot.Obligations)
        {
            Append(canonical, obligation.RequirementId.ToString());
            Append(canonical, obligation.DocumentType);
            Append(canonical, obligation.IsMandatory ? "M" : "O");
            Append(canonical, obligation.Status);

            if (obligation.Evidence is not { } evidence)
            {
                // A distinct marker rather than an empty run of fields. Without it, "no evidence"
                // and "evidence with every field blank" would hash the same.
                Append(canonical, "no-evidence");
                continue;
            }

            Append(canonical, "evidence");
            Append(canonical, evidence.DocumentId.ToString());
            Append(canonical, evidence.CertificateNumber);
            Append(canonical, evidence.Issuer);
            Append(canonical, evidence.HolderName);
            Append(canonical, evidence.IssuedOn.ToString("O", CultureInfo.InvariantCulture));
            Append(canonical, evidence.ExpiresOn.ToString("O", CultureInfo.InvariantCulture));
            Append(canonical, evidence.ApprovedBy);
            Append(canonical, evidence.ApprovedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// <c>DaysRemaining</c> is deliberately absent from the hashed form: it is derived from the
    /// expiry date and the day you ask, so including it would make a report fail its own
    /// verification tomorrow.
    /// </summary>
    private static void Append(StringBuilder canonical, string field) =>
        canonical.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('|');
}

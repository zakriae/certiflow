using Certiflow.SharedKernel;

namespace Certiflow.Reporting.Domain;

/// <summary>
/// Everything a supplier compliance certificate asserts, as of one instant (FR-6.1).
/// <para>
/// This is the report. The PDF is a rendering of it, and <see cref="ReportFingerprint"/> hashes it —
/// so the hash printed on the page attests to these facts and not to the bytes of a particular
/// layout. Change the typeface and the fingerprint is unchanged; change a certificate number and it
/// is not. That is the correct sensitivity for a document whose purpose is to be checked later.
/// </para>
/// </summary>
public sealed record SupplierComplianceSnapshot(
    SupplierId SupplierId,
    string LegalName,
    string? TradingName,
    string RegistrationNumber,
    string CountryCode,
    string CategoryName,
    int ProfileVersion,
    string OverallStatus,
    DateOnly AsOf,
    IReadOnlyList<ObligationLine> Obligations)
{
    public int MandatoryCount => Obligations.Count(o => o.IsMandatory);

    public int SatisfiedMandatoryCount => Obligations.Count(o => o.IsMandatory && o.Status == "Satisfied");
}

/// <summary>One requirement and whatever evidence currently satisfies it.</summary>
public sealed record ObligationLine(
    RequirementId RequirementId,
    string DocumentType,
    bool IsMandatory,
    string Status,
    int? DaysRemaining,
    EvidenceLine? Evidence);

public sealed record EvidenceLine(
    DocumentId DocumentId,
    string CertificateNumber,
    string Issuer,
    string HolderName,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string ApprovedBy,
    DateTimeOffset ApprovedAt)
{
    /// <summary>
    /// The reviewer's name is on the certificate deliberately. A compliance report that says a
    /// document was approved without saying by whom is exactly the report an auditor sends back.
    /// </summary>
    public string Attribution => $"{ApprovedBy} on {ApprovedAt.ToUniversalTime():yyyy-MM-dd}";
}

using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Domain;

/// <summary>
/// An approved Document bound to a Requirement with a Validity Period (SRS §3).
/// <para>
/// Constructing one of these is only possible from an approved verdict — that is why
/// <see cref="ApprovedBy"/> and <see cref="ApprovedAt"/> are required rather than optional. An
/// extraction alone can never produce evidence, no matter how confident it was (SRS §4.3,
/// BC4↔BC5 Partnership).
/// </para>
/// </summary>
public sealed record CertificateEvidence
{
    public CertificateEvidence(
        DocumentId documentId,
        string certificateNumber,
        string issuer,
        string holderName,
        ValidityPeriod validity,
        string approvedBy,
        DateTimeOffset approvedAt)
    {
        DocumentId = documentId;
        CertificateNumber = Guard.AgainstNullOrWhiteSpace(certificateNumber, "compliance.evidence.certificate_number_required");
        Issuer = Guard.AgainstNullOrWhiteSpace(issuer, "compliance.evidence.issuer_required");
        HolderName = Guard.AgainstNullOrWhiteSpace(holderName, "compliance.evidence.holder_required");
        Validity = Guard.AgainstNull(validity, "compliance.evidence.validity_required");
        ApprovedBy = Guard.AgainstNullOrWhiteSpace(approvedBy, "compliance.evidence.approver_required");
        ApprovedAt = approvedAt;
    }

    public DocumentId DocumentId { get; }

    public string CertificateNumber { get; }

    public string Issuer { get; }

    public string HolderName { get; }

    public ValidityPeriod Validity { get; }

    public string ApprovedBy { get; }

    public DateTimeOffset ApprovedAt { get; }
}

/// <summary>
/// Why a piece of evidence stopped being current. Evidence is never deleted — it moves to the
/// obligation's history with one of these reasons (SRS §10.1).
/// </summary>
public enum EvidenceRetirementReason
{
    /// <summary>A newer approved document replaced it.</summary>
    Superseded = 1,

    /// <summary>Its validity period ended.</summary>
    Expired = 2,

    /// <summary>The requirement it satisfied is no longer in the supplier's profile.</summary>
    RequirementNoLongerApplicable = 3,
}

/// <summary>One retired piece of evidence, kept for the status history of FR-5.5 and FR-5.8.</summary>
public sealed record RetiredEvidence(
    CertificateEvidence Evidence,
    EvidenceRetirementReason Reason,
    DateTimeOffset RetiredAt);

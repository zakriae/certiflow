namespace Certiflow.Compliance.Domain;

/// <summary>
/// The state of one obligation.
/// <para>
/// <b>Ordered best-to-worst on purpose.</b> Deriving a supplier's overall status is then
/// literally <c>Max()</c> across mandatory obligations — the worst one wins (SRS §10.1). The
/// ordering is load-bearing, so <see cref="ComplianceStatusMapping"/> tests pin it.
/// </para>
/// </summary>
public enum ObligationStatus
{
    /// <summary>Approved evidence, valid today, with enough validity left to count.</summary>
    Satisfied = 0,

    /// <summary>Still valid, but inside the renewal window or below the minimum validity.</summary>
    AtRisk = 1,

    /// <summary>A document has been submitted but no verdict has been reached yet.</summary>
    AwaitingReview = 2,

    /// <summary>Evidence exists but its validity period has ended.</summary>
    Expired = 3,

    /// <summary>No evidence has ever been supplied.</summary>
    Missing = 4,
}

/// <summary>
/// A supplier's overall compliance status (SRS §3). Always derived from obligations, never
/// assigned — there is no setter and no API that accepts one (SRS §19 Q12).
/// </summary>
public enum ComplianceStatus
{
    /// <summary>No compliance profile has been applied yet, or evidence is under review.</summary>
    Pending = 0,

    Compliant = 1,

    AtRisk = 2,

    NonCompliant = 3,
}

public static class ComplianceStatusMapping
{
    /// <summary>
    /// Collapses the worst obligation status into the supplier-level status shown on the
    /// dashboard. Expired and Missing are both simply non-compliant: an auditor does not care
    /// whether the certificate lapsed or never arrived.
    /// </summary>
    public static ComplianceStatus ToComplianceStatus(this ObligationStatus status) => status switch
    {
        ObligationStatus.Satisfied => ComplianceStatus.Compliant,
        ObligationStatus.AtRisk => ComplianceStatus.AtRisk,
        ObligationStatus.AwaitingReview => ComplianceStatus.Pending,
        ObligationStatus.Expired => ComplianceStatus.NonCompliant,
        ObligationStatus.Missing => ComplianceStatus.NonCompliant,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped obligation status."),
    };
}

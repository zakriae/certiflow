using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Domain;

/// <summary>
/// BC5's own copy of one Requirement, built from BC1's <c>RequirementDescriptor</c> when a
/// profile version is published.
/// <para>
/// This is the Published Language translation of SRS §4.3: Compliance owns its read model and
/// never queries the Supplier Registry's database to evaluate a supplier. The duplication is
/// the point — it is what lets BC1 change its model without breaking compliance evaluation.
/// </para>
/// </summary>
public sealed record RequirementSpecification
{
    public RequirementSpecification(
        RequirementId requirementId,
        string documentType,
        bool isMandatory,
        int renewalLeadTimeDays,
        int minValidityDays)
    {
        RequirementId = requirementId;
        DocumentType = Guard.AgainstNullOrWhiteSpace(documentType, "compliance.requirement.document_type_required");
        IsMandatory = isMandatory;

        // SRS §6.1: RenewalLeadTimeDays in 1..365. Zero would mean "warn me the day it expires",
        // which defeats the purpose of a lead time.
        RenewalLeadTimeDays = Guard.AgainstOutOfRange(
            renewalLeadTimeDays, 1, 365, "compliance.requirement.lead_time_out_of_range");

        MinValidityDays = Guard.AgainstOutOfRange(
            minValidityDays, 0, 365, "compliance.requirement.min_validity_out_of_range");
    }

    public RequirementId RequirementId { get; }

    public string DocumentType { get; }

    /// <summary>
    /// Non-mandatory obligations never make a supplier non-compliant (SRS §10.1). They are still
    /// tracked and still generate reminders.
    /// </summary>
    public bool IsMandatory { get; }

    /// <summary>Days before expiry at which the obligation becomes At Risk.</summary>
    public int RenewalLeadTimeDays { get; }

    /// <summary>
    /// Evidence must have at least this many days left to count as Satisfied. A certificate
    /// expiring next week does not really satisfy a requirement, even though it is technically
    /// still valid today.
    /// </summary>
    public int MinValidityDays { get; }
}

namespace Certiflow.Compliance.Domain.Tests;

/// <summary>
/// Test data builders. Every test reads as "given this evidence and these thresholds, the status
/// is X" — the noise of constructing ids and dates lives here instead of in the assertions.
/// </summary>
internal static class ComplianceScenario
{
    public static readonly DateOnly Today = new(2026, 8, 18);

    public static readonly DateTimeOffset Now = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    public static readonly Guid LogisticsCategory = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static SupplierId Supplier(string seed = "aaaaaaaa") =>
        new(Guid.Parse($"{seed}-0000-0000-0000-000000000001"));

    public static RequirementId Requirement(int n) =>
        new(Guid.Parse($"00000000-0000-0000-0000-0000000000{n:D2}"));

    public static DocumentId Document(int n) =>
        new(Guid.Parse($"00000000-0000-0000-0000-0000000001{n:D2}"));

    public static RequirementSpecification Spec(
        int requirement = 1,
        string documentType = "ISO 9001",
        bool mandatory = true,
        int renewalLeadTimeDays = 30,
        int minValidityDays = 0) =>
        new(Requirement(requirement), documentType, mandatory, renewalLeadTimeDays, minValidityDays);

    public static CertificateEvidence Evidence(
        int document = 1,
        DateOnly? issuedOn = null,
        DateOnly? expiresOn = null,
        string holderName = "Meridian Logistics SARL",
        string issuer = "AFNOR Certification",
        string approvedBy = "reviewer@certiflow.demo") =>
        new(
            Document(document),
            certificateNumber: $"CERT-{document:D5}",
            issuer: issuer,
            holderName: holderName,
            validity: new ValidityPeriod(
                issuedOn ?? Today.AddYears(-1),
                expiresOn ?? Today.AddDays(365)),
            approvedBy: approvedBy,
            approvedAt: Now);

    /// <summary>A registered supplier with one published profile version and no evidence yet.</summary>
    public static SupplierComplianceState WithProfile(params RequirementSpecification[] requirements)
    {
        var state = SupplierComplianceState.Register(Supplier(), LogisticsCategory);
        state.ApplyProfileVersion(1, requirements, Today, Now);
        state.ClearDomainEvents();
        return state;
    }
}

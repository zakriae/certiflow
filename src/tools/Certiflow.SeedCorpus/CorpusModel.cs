namespace Certiflow.SeedCorpus;

/// <summary>
/// Certificate language. The corpus is deliberately bilingual: a French certificate that the
/// pipeline reads correctly demonstrates the French-market capability far more convincingly than
/// a language toggle on the UI, and it exercises the diacritic folding that grounding depends on.
/// </summary>
public enum CorpusLanguage
{
    English = 1,
    French = 2,
}

/// <summary>
/// Which template a certificate is rendered with. Several exist on purpose — an extraction
/// pipeline that only works against one layout has been fitted to the layout, not to the problem.
/// </summary>
public enum CertificateLayout
{
    /// <summary>Formal, centred, heavy rules. What a certification body actually issues.</summary>
    Classic = 1,

    /// <summary>Left-aligned with a label/value table. Common for insurance and licences.</summary>
    Tabular = 2,

    /// <summary>Dense single-block layout with the details in a paragraph.</summary>
    Compact = 3,
}

/// <summary>What happened to a submitted document in the seeded history.</summary>
public enum SeedDocumentOutcome
{
    /// <summary>Reviewed and approved. Becomes evidence.</summary>
    Approved = 1,

    /// <summary>Submitted, extraction complete, sitting in the review queue.</summary>
    AwaitingReview = 2,
}

public enum SeedSupplierStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>The compliance status a supplier is <em>designed</em> to land on (SRS §16.1).</summary>
public enum ExpectedComplianceStatus
{
    Compliant = 1,
    AtRisk = 2,
    NonCompliant = 3,
    Pending = 4,
}

public sealed record SeedRequirement(
    Guid RequirementId,
    string DocumentType,
    bool IsMandatory,
    int RenewalLeadTimeDays,
    int MinValidityDays,
    bool RequiresIssuerMatch,
    IReadOnlyList<string> AcceptedIssuers);

public sealed record SeedCategory(
    Guid CategoryId,
    string Name,
    int ProfileVersion,
    IReadOnlyList<SeedRequirement> Requirements);

/// <summary>
/// One certificate: both the facts it asserts and the file it is rendered into. Every field here
/// is something the extraction pipeline will be asked to read back out of the PDF, which is why
/// the tests assert each one is findable in the generated text layer.
/// </summary>
public sealed record SeedCertificate(
    Guid DocumentId,
    Guid RequirementId,
    string DocumentType,
    string FileName,
    string CertificateNumber,
    string IssuerName,
    string HolderName,
    string HolderAddress,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string Scope,
    string? Standard,
    SeedDocumentOutcome Outcome,
    CertificateLayout Layout,
    CorpusLanguage Language,
    string? DemoNote);

public sealed record SeedSupplier(
    Guid SupplierId,
    string LegalName,
    string? TradingName,
    string RegistrationNumber,
    string CountryCode,
    Guid CategoryId,
    SeedSupplierStatus Status,
    string ContactName,
    string ContactEmail,
    CorpusLanguage Language,
    ExpectedComplianceStatus ExpectedStatus,
    string DemoRole,
    IReadOnlyList<SeedCertificate> Certificates);

/// <summary>
/// The whole corpus. Serialised alongside the PDFs as <c>manifest.json</c> so the database seeder
/// consumes data rather than re-deriving it, and so a human can see what the demo is supposed to
/// show without opening thirty files.
/// </summary>
public sealed record SeedCorpusManifest(
    DateOnly ReferenceDate,
    int Seed,
    IReadOnlyList<SeedCategory> Categories,
    IReadOnlyList<SeedSupplier> Suppliers);

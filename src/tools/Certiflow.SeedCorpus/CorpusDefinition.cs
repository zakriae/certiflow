namespace Certiflow.SeedCorpus;

/// <summary>
/// Builds the corpus of SRS §16.1: three categories, twelve suppliers, and a deliberate spread of
/// compliance states so the dashboard looks like a real portfolio on first load rather than a
/// column of green.
/// <para>
/// Every date is expressed relative to a reference date, never hard-coded. A corpus with fixed
/// dates is correct for one week and quietly wrong afterwards — the "expires in 9 days" supplier
/// that the demo turns on would silently become "expired four months ago" by the time anyone
/// watched the video.
/// </para>
/// </summary>
public static class CorpusDefinition
{
    private static readonly string[] CertificationBodies =
        ["AFNOR Certification", "Bureau Veritas Certification", "SGS United Kingdom Ltd", "DEKRA Certification", "TÜV Rheinland"];

    private static readonly string[] Insurers =
        ["Allianz Global Corporate", "AXA Entreprises", "Zurich Insurance plc", "Hiscox Underwriting Ltd"];

    private static readonly string[] Authorities =
        ["Chambre de Commerce et d'Industrie", "Companies House", "Registre du Commerce et des Sociétés"];

    // ── Categories ───────────────────────────────────────────────────────────────────────────

    public static SeedCategory Logistics { get; } = new(
        DeterministicId.For("category:logistics-contractor"),
        "Logistics Contractor",
        ProfileVersion: 3,
        [
            Requirement("logistics", "ISO 9001", mandatory: true, leadTime: 60, minValidity: 30, CertificationBodies),
            Requirement("logistics", "Public Liability Insurance", mandatory: true, leadTime: 30, minValidity: 14, Insurers),
            Requirement("logistics", "Trade Licence", mandatory: true, leadTime: 45, minValidity: 30, Authorities),
            Requirement("logistics", "ISO 14001", mandatory: false, leadTime: 60, minValidity: 0, CertificationBodies),
        ]);

    public static SeedCategory Facilities { get; } = new(
        DeterministicId.For("category:facilities-services"),
        "Facilities Services",
        ProfileVersion: 2,
        [
            Requirement("facilities", "ISO 45001", mandatory: true, leadTime: 60, minValidity: 30, CertificationBodies),
            Requirement("facilities", "Public Liability Insurance", mandatory: true, leadTime: 30, minValidity: 14, Insurers),
            Requirement("facilities", "Safety Training Record", mandatory: true, leadTime: 30, minValidity: 0, CertificationBodies),
        ]);

    public static SeedCategory Food { get; } = new(
        DeterministicId.For("category:food-supplier"),
        "Food Supplier",
        ProfileVersion: 4,
        [
            Requirement("food", "Food Hygiene Certificate", mandatory: true, leadTime: 45, minValidity: 30, CertificationBodies),
            Requirement("food", "ISO 9001", mandatory: true, leadTime: 60, minValidity: 30, CertificationBodies),
            Requirement("food", "Public Liability Insurance", mandatory: true, leadTime: 30, minValidity: 14, Insurers),
        ]);

    public static IReadOnlyList<SeedCategory> Categories { get; } = [Logistics, Facilities, Food];

    private static SeedRequirement Requirement(
        string category,
        string documentType,
        bool mandatory,
        int leadTime,
        int minValidity,
        IReadOnlyList<string> acceptedIssuers) =>
        new(
            DeterministicId.For($"requirement:{category}:{documentType}"),
            documentType,
            mandatory,
            leadTime,
            minValidity,
            RequiresIssuerMatch: true,
            acceptedIssuers);

    // ── Suppliers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The twelve suppliers. The <c>demoRole</c> on each says what it is there to demonstrate, so
    /// nobody later "tidies up" the supplier whose certificate is deliberately wrong.
    /// </summary>
    public static IReadOnlyList<SeedSupplier> Suppliers(DateOnly today) =>
    [
        // ── 5 compliant ──────────────────────────────────────────────────────────────────────
        Build(
            "Meridian Logistics SARL", "Meridian Freight", "FR-RCS-812449721", "FR", Logistics,
            CorpusLanguage.French, ExpectedComplianceStatus.Compliant,
            "the healthy supplier the demo opens on",
            today, [Cert("ISO 9001", +402), Cert("Public Liability Insurance", +198), Cert("Trade Licence", +260), Cert("ISO 14001", +330)]),

        Build(
            "Northgate Distribution Ltd", null, "GB-09847213", "GB", Logistics,
            CorpusLanguage.English, ExpectedComplianceStatus.Compliant,
            "second compliant logistics supplier, English certificates",
            today, [Cert("ISO 9001", +511), Cert("Public Liability Insurance", +140), Cert("Trade Licence", +288)]),

        Build(
            "Vantage Facilities Ltd", "Vantage FM", "GB-07731905", "GB", Facilities,
            CorpusLanguage.English, ExpectedComplianceStatus.Compliant,
            "compliant facilities supplier",
            today, [Cert("ISO 45001", +366), Cert("Public Liability Insurance", +221), Cert("Safety Training Record", +180)]),

        Build(
            "Maison Delacroix Traiteur", "Delacroix", "FR-RCS-448120933", "FR", Food,
            CorpusLanguage.French, ExpectedComplianceStatus.Compliant,
            "compliant food supplier, French certificates with accented text",
            today, [Cert("Food Hygiene Certificate", +295), Cert("ISO 9001", +430), Cert("Public Liability Insurance", +165)]),

        Build(
            "Brightleaf Produce Co", null, "GB-11204488", "GB", Food,
            CorpusLanguage.English, ExpectedComplianceStatus.Compliant,
            "compliant food supplier",
            today, [Cert("Food Hygiene Certificate", +240), Cert("ISO 9001", +388), Cert("Public Liability Insurance", +112)]),

        // ── 2 at risk ────────────────────────────────────────────────────────────────────────
        Build(
            "Northwind Freight Ltd", null, "GB-06612348", "GB", Logistics,
            CorpusLanguage.English, ExpectedComplianceStatus.AtRisk,
            "AT RISK — ISO 9001 expires in 9 days. The supplier the demo drills into",
            today, [Cert("ISO 9001", +9), Cert("Public Liability Insurance", +205), Cert("Trade Licence", +301)]),

        Build(
            "Groupe Nettoyage Rivière", "Rivière Services", "FR-RCS-390277154", "FR", Facilities,
            CorpusLanguage.French, ExpectedComplianceStatus.AtRisk,
            "AT RISK — insurance expires in 21 days",
            today, [Cert("ISO 45001", +410), Cert("Public Liability Insurance", +21), Cert("Safety Training Record", +150)]),

        // ── 2 non-compliant ──────────────────────────────────────────────────────────────────
        Build(
            "Cedar Haulage Group", null, "GB-04412907", "GB", Logistics,
            CorpusLanguage.English, ExpectedComplianceStatus.NonCompliant,
            "NON-COMPLIANT — ISO 9001 expired 34 days ago",
            today, [Cert("ISO 9001", -34), Cert("Public Liability Insurance", +176), Cert("Trade Licence", +254)]),

        Build(
            "Halcyon Maintenance Ltd", null, "GB-10093321", "GB", Facilities,
            CorpusLanguage.English, ExpectedComplianceStatus.NonCompliant,
            "NON-COMPLIANT — no Safety Training Record has ever been supplied",
            today, [Cert("ISO 45001", +344), Cert("Public Liability Insurance", +190)]),

        // ── 2 awaiting review ────────────────────────────────────────────────────────────────
        Build(
            "Sterling Site Services", null, "GB-08820114", "GB", Facilities,
            CorpusLanguage.English, ExpectedComplianceStatus.Pending,
            "AWAITING REVIEW — the certificate's holder name is deliberately wrong",
            today,
            [
                Cert("ISO 45001", +377, Outcome: SeedDocumentOutcome.AwaitingReview,
                     HolderOverride: "Sterling Site Services Group Ltd",
                     Note: "Holder name reads 'Sterling Site Services Group Ltd' against a supplier "
                         + "registered as 'Sterling Site Services'. Entity match should fail and send "
                         + "this to a reviewer instead of auto-accepting (SRS §16.1)."),
                Cert("Public Liability Insurance", +233),
                Cert("Safety Training Record", +164),
            ]),

        Build(
            "Oakfield Dairy Co", null, "GB-05590228", "GB", Food,
            CorpusLanguage.English, ExpectedComplianceStatus.Pending,
            "AWAITING REVIEW — routine submission still in the queue",
            today,
            [
                Cert("Food Hygiene Certificate", +318, Outcome: SeedDocumentOutcome.AwaitingReview),
                Cert("ISO 9001", +455),
                Cert("Public Liability Insurance", +129),
            ]),

        // ── 1 suspended ──────────────────────────────────────────────────────────────────────
        Build(
            "Ravenna Foods SpA", null, "IT-MI-2019447", "IT", Food,
            CorpusLanguage.English, ExpectedComplianceStatus.NonCompliant,
            "SUSPENDED — retained to show suspension stops notifications without deleting history",
            today, [Cert("Food Hygiene Certificate", -88), Cert("ISO 9001", +210), Cert("Public Liability Insurance", +95)],
            SeedSupplierStatus.Suspended),
    ];

    /// <summary>A certificate expressed as "expires this many days from the reference date".</summary>
    private sealed record CertSpec(
        string DocumentType,
        int ExpiresInDays,
        SeedDocumentOutcome Outcome = SeedDocumentOutcome.Approved,
        string? HolderOverride = null,
        string? Note = null);

    private static CertSpec Cert(
        string documentType,
        int expiresInDays,
        SeedDocumentOutcome Outcome = SeedDocumentOutcome.Approved,
        string? HolderOverride = null,
        string? Note = null) =>
        new(documentType, expiresInDays, Outcome, HolderOverride, Note);

    private static SeedSupplier Build(
        string legalName,
        string? tradingName,
        string registrationNumber,
        string countryCode,
        SeedCategory category,
        CorpusLanguage language,
        ExpectedComplianceStatus expectedStatus,
        string demoRole,
        DateOnly today,
        IReadOnlyList<CertSpec> certificates,
        SeedSupplierStatus status = SeedSupplierStatus.Active)
    {
        var supplierId = DeterministicId.For($"supplier:{legalName}");
        var slug = Slug(legalName);
        var contactSurname = legalName.Split(' ')[0];

        var built = certificates.Select((spec, index) =>
        {
            var requirement = category.Requirements.Single(r => r.DocumentType == spec.DocumentType);

            // Validity spans differ by document type: certification bodies issue three-year ISO
            // certificates, insurers and licensing authorities work in twelve-month terms.
            var validityDays = spec.DocumentType.StartsWith("ISO", StringComparison.Ordinal) ? 1095 : 365;
            var expiresOn = today.AddDays(spec.ExpiresInDays);
            var issuedOn = expiresOn.AddDays(-validityDays);

            var issuer = requirement.AcceptedIssuers[index % requirement.AcceptedIssuers.Count];
            var layout = (CertificateLayout)((index % 3) + 1);

            return new SeedCertificate(
                DocumentId: DeterministicId.For($"document:{legalName}:{spec.DocumentType}"),
                RequirementId: requirement.RequirementId,
                DocumentType: spec.DocumentType,
                FileName: $"{slug}-{Slug(spec.DocumentType)}.pdf",
                CertificateNumber: CertificateNumber(countryCode, spec.DocumentType, legalName),
                IssuerName: issuer,
                HolderName: spec.HolderOverride ?? legalName,
                HolderAddress: Address(countryCode, legalName),
                IssuedOn: issuedOn,
                ExpiresOn: expiresOn,
                Scope: Scope(spec.DocumentType, category.Name),
                Standard: Standard(spec.DocumentType),
                Outcome: spec.Outcome,
                Layout: layout,
                Language: language,
                DemoNote: spec.Note);
        }).ToList();

        return new SeedSupplier(
            supplierId,
            legalName,
            tradingName,
            registrationNumber,
            countryCode,
            category.CategoryId,
            status,
            ContactName: $"{FirstName(legalName)} {contactSurname}",
            ContactEmail: $"contact@{slug}.demo",
            language,
            expectedStatus,
            demoRole,
            built);
    }

    // ── Deterministic field values ───────────────────────────────────────────────────────────
    // Derived from the supplier name rather than randomised, so the corpus regenerates identically
    // without threading a Random through every call site.

    private static string CertificateNumber(string countryCode, string documentType, string legalName)
    {
        var prefix = documentType switch
        {
            "ISO 9001" => "QMS",
            "ISO 14001" => "EMS",
            "ISO 45001" => "OHS",
            "Public Liability Insurance" => "PLI",
            "Trade Licence" => "TRD",
            "Safety Training Record" => "STR",
            "Food Hygiene Certificate" => "FHC",
            _ => "DOC",
        };

        var digits = (DeterministicId.StableInt($"certnumber:{legalName}:{documentType}") % 900000) + 100000;

        return $"{countryCode}-{prefix}-{digits}";
    }

    private static string Address(string countryCode, string legalName)
    {
        var number = (DeterministicId.StableInt($"address:{legalName}") % 180) + 3;

        return countryCode switch
        {
            "FR" => $"{number} rue de l'Industrie, 69007 Lyon, France",
            "IT" => $"Via Meccanica {number}, 20139 Milano, Italia",
            _ => $"{number} Fairfield Way, Manchester M15 4QL, United Kingdom",
        };
    }

    private static string FirstName(string legalName) =>
        (DeterministicId.StableInt($"contact:{legalName}") % 6) switch
        {
            0 => "Camille",
            1 => "Rachid",
            2 => "Eleanor",
            3 => "Thomas",
            4 => "Ines",
            _ => "Marcus",
        };

    private static string? Standard(string documentType) =>
        documentType switch
        {
            "ISO 9001" => "ISO 9001:2015",
            "ISO 14001" => "ISO 14001:2015",
            "ISO 45001" => "ISO 45001:2018",
            _ => null,
        };

    private static string Scope(string documentType, string categoryName) =>
        documentType switch
        {
            "ISO 9001" => $"Provision of {categoryName.ToLowerInvariant()} services including planning, execution and customer support",
            "ISO 14001" => "Environmental management of depot operations, fleet maintenance and waste handling",
            "ISO 45001" => "Occupational health and safety management for site-based service delivery",
            "Public Liability Insurance" => "Public and products liability, limit of indemnity GBP 10,000,000 any one occurrence",
            "Trade Licence" => "Authorisation to operate as a commercial carrier of goods for hire and reward",
            "Safety Training Record" => "Working at height, manual handling and COSHH awareness for all operational staff",
            "Food Hygiene Certificate" => "Storage, preparation and distribution of chilled and ambient food products",
            _ => "General scope of operations",
        };

    private static string Slug(string value)
    {
        var slug = new System.Text.StringBuilder(value.Length);
        var lastWasDash = true;

        foreach (var c in value.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                slug.Append('-');
                lastWasDash = true;
            }
        }

        return slug.ToString().Trim('-');
    }
}

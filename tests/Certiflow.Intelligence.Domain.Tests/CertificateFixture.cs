using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// A synthetic ISO 9001 certificate, in the two forms the pipeline needs: the parsed text of the
/// PDF, and the raw values a model claims to have read from it.
/// <para>
/// Every test varies exactly one thing — a date, a name, a citation — and the document text is
/// generated from the same values as the candidates, so a "good" extraction genuinely grounds
/// rather than grounding because the fixture was written to agree with itself.
/// </para>
/// </summary>
internal sealed record CertificateFixture
{
    public static readonly DateOnly Today = new(2026, 8, 18);

    public string Holder { get; init; } = "Meridian Logistics SARL";

    public string Issuer { get; init; } = "AFNOR Certification";

    public string CertificateNumber { get; init; } = "FR-9001-00417";

    public string IssuedOn { get; init; } = "2025-03-14";

    public string ExpiresOn { get; init; } = "2027-03-13";

    public string Standard { get; init; } = "ISO 9001:2015";

    public string Scope { get; init; } = "Road freight transport and warehousing";

    public string SupplierLegalName { get; init; } = "Meridian Logistics SARL";

    public string? SupplierTradingName { get; init; }

    /// <summary>The schema of SRS §8.3, as a declarative field list (FR-3.9).</summary>
    public static DocumentTypeSchema Schema() => new(
        "ISO 9001",
        "2026-08-01",
        [
            new FieldDefinition(
                CertificateFieldNames.HolderName,
                FieldValueType.Text,
                isMandatory: true,
                entityMatch: EntityMatchTarget.SupplierName),
            new FieldDefinition(
                CertificateFieldNames.IssuerName,
                FieldValueType.Text,
                isMandatory: true,
                entityMatch: EntityMatchTarget.AcceptedIssuer),
            new FieldDefinition(
                CertificateFieldNames.CertificateNumber,
                FieldValueType.Text,
                isMandatory: true,
                pattern: "^[A-Z0-9/-]{4,32}$"),
            new FieldDefinition(CertificateFieldNames.IssuedOn, FieldValueType.Date, isMandatory: true),
            new FieldDefinition(CertificateFieldNames.ExpiresOn, FieldValueType.Date, isMandatory: true),
            new FieldDefinition(
                CertificateFieldNames.Standard,
                FieldValueType.Enumeration,
                isMandatory: true,
                allowedValues: ["ISO 9001:2015", "ISO 14001:2015", "ISO 45001:2018"]),
            new FieldDefinition(CertificateFieldNames.Scope, FieldValueType.Text, isMandatory: false),
        ]);

    /// <summary>
    /// The PDF's text layer, laid out as a real certificate is: identity on page 1, the
    /// machine-readable detail on page 2.
    /// </summary>
    public ParsedDocument Document() => new(
        [
            new DocumentPage(1, $"""
                CERTIFICATE OF REGISTRATION
                {Standard}

                This is to certify that the Quality Management System of

                {Holder}

                has been assessed and found to conform to the requirements of the standard above.
                """),
            new DocumentPage(2, $"""
                Certificate Number: {CertificateNumber}
                Date of Issue: {IssuedOn}
                Expiry Date: {ExpiresOn}
                Scope: {Scope}
                Issued by: {Issuer}
                """),
        ],
        TextSource.EmbeddedTextLayer);

    /// <summary>An honest extraction: every value real, every citation pointing at real text.</summary>
    public IReadOnlyList<FieldCandidate> Candidates() =>
    [
        new(CertificateFieldNames.HolderName, Holder, new Citation(1, Holder)),
        new(CertificateFieldNames.IssuerName, Issuer, new Citation(2, $"Issued by: {Issuer}")),
        new(CertificateFieldNames.CertificateNumber, CertificateNumber, new Citation(2, $"Certificate Number: {CertificateNumber}")),
        new(CertificateFieldNames.IssuedOn, IssuedOn, new Citation(2, $"Date of Issue: {IssuedOn}")),
        new(CertificateFieldNames.ExpiresOn, ExpiresOn, new Citation(2, $"Expiry Date: {ExpiresOn}")),
        new(CertificateFieldNames.Standard, Standard, new Citation(1, Standard)),
        new(CertificateFieldNames.Scope, Scope, new Citation(2, $"Scope: {Scope}")),
    ];

    public ExtractionContext Context(bool requiresIssuerMatch = true) => new(
        SupplierLegalName,
        SupplierTradingName,
        acceptedIssuers: ["AFNOR Certification", "Bureau Veritas", "SGS"],
        requiresIssuerMatch,
        expectedStandard: "ISO 9001:2015",
        Today);

    public IReadOnlyList<ExtractedField> Evaluate(
        IReadOnlyCollection<FieldCandidate>? candidates = null,
        bool requiresIssuerMatch = true) =>
        FieldEvaluator.Evaluate(
            Schema(),
            candidates ?? Candidates(),
            Document(),
            Context(requiresIssuerMatch));

    /// <summary>Replaces one candidate, leaving the rest of an honest extraction intact.</summary>
    public IReadOnlyList<FieldCandidate> CandidatesWith(FieldCandidate replacement) =>
    [
        .. Candidates().Where(c => !string.Equals(c.FieldName, replacement.FieldName, StringComparison.Ordinal)),
        replacement,
    ];
}

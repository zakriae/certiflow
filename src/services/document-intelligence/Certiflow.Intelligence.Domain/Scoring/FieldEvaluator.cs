using System.Globalization;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Scoring;

/// <summary>
/// One raw value as the model returned it, before anything has been verified.
/// <para>
/// Note what is <em>not</em> here: a confidence. The model is never asked how sure it is, so there
/// is no field on this type to put the answer in (SRS §19 Q4).
/// </para>
/// </summary>
public sealed record FieldCandidate(
    string FieldName,
    string? RawValue,
    Citation? Citation,
    string? SecondPassValue = null);

/// <summary>
/// Everything outside the document that the checks need: who the supplier is supposed to be,
/// which issuers the requirement accepts, and what today is.
/// </summary>
public sealed record ExtractionContext
{
    public ExtractionContext(
        string supplierLegalName,
        string? supplierTradingName,
        IReadOnlyList<string>? acceptedIssuers,
        bool requiresIssuerMatch,
        string? expectedStandard,
        DateOnly today)
    {
        SupplierLegalName = Guard.AgainstNullOrWhiteSpace(
            supplierLegalName, "intelligence.context.supplier_name_required");

        SupplierTradingName = supplierTradingName;
        AcceptedIssuers = acceptedIssuers ?? [];
        RequiresIssuerMatch = requiresIssuerMatch;
        ExpectedStandard = expectedStandard;
        Today = today;

        Guard.Require(
            !requiresIssuerMatch || AcceptedIssuers.Count > 0,
            "intelligence.context.issuer_match_without_issuers",
            "The requirement demands an issuer match but supplies no accepted issuers.");
    }

    public string SupplierLegalName { get; }

    public string? SupplierTradingName { get; }

    public IReadOnlyList<string> AcceptedIssuers { get; }

    public bool RequiresIssuerMatch { get; }

    /// <summary>
    /// The standard the Requirement asks for, e.g. <c>ISO 9001:2015</c>. Null when the document
    /// type has no standard to match.
    /// </summary>
    public string? ExpectedStandard { get; }

    public DateOnly Today { get; }
}

/// <summary>
/// <b>Turns the model's raw claims into scored, grounded fields (SRS §8.4).</b>
/// <para>
/// Runs in two passes, because the checks have different scopes. Grounding, type validity and
/// entity match are per-field. Cross-field consistency is not: whether an expiry date is
/// plausible depends on the issue date, which is a different field. So every field is parsed
/// first, and only then is consistency scored across the parsed set.
/// </para>
/// <para>
/// Entirely pure. Same inputs, same outputs, no clock, no I/O — which is why the tests for the
/// hard part of this product are ordinary unit tests.
/// </para>
/// </summary>
public static class FieldEvaluator
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Accepted input formats, widest first. French certificates print <c>31/12/2026</c>; the
    /// structured-output schema asks for ISO but the model does not always comply, and rejecting
    /// a correctly-read date over its format would be a false negative charged to the reviewer.
    /// </summary>
    private static readonly string[] AcceptedDateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
        "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "yyyyMMdd",
    ];

    public static IReadOnlyList<ExtractedField> Evaluate(
        DocumentTypeSchema schema,
        IReadOnlyCollection<FieldCandidate> candidates,
        ParsedDocument document,
        ExtractionContext context)
    {
        Guard.AgainstNull(schema, "intelligence.evaluator.schema_required");
        Guard.AgainstNull(candidates, "intelligence.evaluator.candidates_required");
        Guard.AgainstNull(document, "intelligence.evaluator.document_required");
        Guard.AgainstNull(context, "intelligence.evaluator.context_required");

        // Pass 1: ground and parse each field the schema declares. Candidates the schema does not
        // declare are ignored — a model returning extra keys must not be able to widen the
        // contract, which is the entire point of a declarative schema.
        var assessments = schema.Fields
            .Select(definition => Assess(definition, FindCandidate(candidates, definition.Name), document))
            .ToList();

        // Pass 2: score consistency across the parsed values, then fold everything into a score.
        var parsedDates = assessments
            .Where(a => a.Definition.ValueType == FieldValueType.Date && a.TypedValue is not null)
            .ToDictionary(
                a => a.Definition.Name,
                a => DateOnly.ParseExact(a.TypedValue!, DateFormat, CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);

        return [.. assessments.Select(a => Finalize(a, parsedDates, context))];
    }

    private static FieldCandidate? FindCandidate(IReadOnlyCollection<FieldCandidate> candidates, string name) =>
        candidates.FirstOrDefault(c => string.Equals(c.FieldName, name, StringComparison.OrdinalIgnoreCase));

    private static FieldAssessment Assess(
        FieldDefinition definition,
        FieldCandidate? candidate,
        ParsedDocument document)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.RawValue))
        {
            return new FieldAssessment(definition, candidate, GroundingCheck: null, TypedValue: null);
        }

        var grounding = GroundingVerifier.Verify(candidate.Citation, document);
        var typedValue = TryTypeValue(definition, candidate.RawValue);

        return new FieldAssessment(definition, candidate, grounding, typedValue);
    }

    /// <summary>
    /// Parses to the declared type and returns a canonical string, or null if it does not parse.
    /// Dates come back as ISO-8601 so everything downstream compares like with like.
    /// </summary>
    private static string? TryTypeValue(FieldDefinition definition, string rawValue)
    {
        var value = rawValue.Trim();

        switch (definition.ValueType)
        {
            case FieldValueType.Date:
                return DateOnly.TryParseExact(
                    value, AcceptedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date.ToString(DateFormat, CultureInfo.InvariantCulture)
                    : null;

            case FieldValueType.Enumeration:
                var allowed = definition.AllowedValues
                    .FirstOrDefault(a => string.Equals(
                        TextNormalizer.Normalize(a), TextNormalizer.Normalize(value), StringComparison.Ordinal));

                return allowed;

            case FieldValueType.Text:
                return definition.MatchesPattern(value) ? value : null;

            default:
                throw new DomainRuleViolationException(
                    "intelligence.evaluator.unknown_value_type",
                    $"Field '{definition.Name}' declares an unsupported value type {definition.ValueType}.");
        }
    }

    private static ExtractedField Finalize(
        FieldAssessment assessment,
        IReadOnlyDictionary<string, DateOnly> parsedDates,
        ExtractionContext context)
    {
        var definition = assessment.Definition;

        if (assessment.Candidate is null || string.IsNullOrWhiteSpace(assessment.Candidate.RawValue))
        {
            return ExtractedField.NotReturned(definition.Name, definition.IsMandatory);
        }

        var grounding = assessment.GroundingCheck!;
        var signals = new List<SignalOutcome>
        {
            grounding.IsGrounded
                ? SignalOutcome.Pass(ConfidenceSignal.Grounding, GroundingDetail(grounding))
                : SignalOutcome.Fail(ConfidenceSignal.Grounding, grounding.Detail),
            assessment.TypedValue is not null
                ? SignalOutcome.Pass(ConfidenceSignal.TypeValidity)
                : SignalOutcome.Fail(
                    ConfidenceSignal.TypeValidity,
                    $"'{assessment.Candidate.RawValue}' is not a valid {definition.ValueType.ToString().ToLowerInvariant()} for this field."),
        };

        var consistency = ScoreConsistency(assessment, parsedDates, context);

        if (consistency is not null)
        {
            signals.Add(consistency);
        }

        var entityMatch = ScoreEntityMatch(assessment, context);

        if (entityMatch is not null)
        {
            signals.Add(entityMatch);
        }

        // Only reported when a second pass actually ran (FR-3.10 is a Could). Absent signals are
        // renormalised away rather than counted as failures — see ConfidenceCalculator.
        if (assessment.Candidate.SecondPassValue is not null)
        {
            var agrees = string.Equals(
                TextNormalizer.Normalize(assessment.Candidate.SecondPassValue),
                TextNormalizer.Normalize(assessment.Candidate.RawValue),
                StringComparison.Ordinal);

            signals.Add(agrees
                ? SignalOutcome.Pass(ConfidenceSignal.ModelAgreement)
                : SignalOutcome.Fail(
                    ConfidenceSignal.ModelAgreement,
                    $"A second pass returned '{assessment.Candidate.SecondPassValue}' instead."));
        }

        var breakdown = ConfidenceCalculator.Compute(signals);

        return new ExtractedField(
            definition.Name,
            assessment.Candidate.RawValue,
            assessment.TypedValue,
            definition.IsMandatory,
            breakdown.Confidence,
            grounding.Result,
            grounding.IsGrounded ? grounding.Citation : assessment.Candidate.Citation,
            breakdown.Signals,
            breakdown.VetoReason ?? grounding.Detail);
    }

    private static string? GroundingDetail(GroundingCheck grounding) =>
        grounding.PageMismatch ? grounding.Detail : null;

    /// <summary>
    /// Cross-field consistency (weight 0.20). Only the fields that actually participate in a
    /// consistency rule report this signal; for the others it is genuinely not applicable, and
    /// reporting a pass would inflate their scores with a check that never ran.
    /// </summary>
    private static SignalOutcome? ScoreConsistency(
        FieldAssessment assessment,
        IReadOnlyDictionary<string, DateOnly> parsedDates,
        ExtractionContext context)
    {
        var name = assessment.Definition.Name;

        if (string.Equals(name, CertificateFieldNames.ExpiresOn, StringComparison.OrdinalIgnoreCase))
        {
            if (assessment.TypedValue is null)
            {
                return SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency, "Expiry date could not be parsed, so it cannot be checked.");
            }

            var expiresOn = parsedDates[name];

            if (!parsedDates.TryGetValue(CertificateFieldNames.IssuedOn, out var issuedOn))
            {
                return SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency, "No usable issue date to check the expiry against.");
            }

            if (expiresOn <= issuedOn)
            {
                return SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency,
                    $"Expiry {expiresOn:yyyy-MM-dd} is not after the issue date {issuedOn:yyyy-MM-dd}.");
            }

            // The same five-year bound BC5's ValidityPeriod enforces. A longer span is far more
            // likely to be a mis-read year than a real certificate.
            if (expiresOn > issuedOn.AddYears(5))
            {
                return SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency,
                    $"Validity span {issuedOn:yyyy-MM-dd} → {expiresOn:yyyy-MM-dd} is implausibly long.");
            }

            return SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency);
        }

        if (string.Equals(name, CertificateFieldNames.IssuedOn, StringComparison.OrdinalIgnoreCase))
        {
            if (assessment.TypedValue is null)
            {
                return SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency, "Issue date could not be parsed, so it cannot be checked.");
            }

            var issuedOn = parsedDates[name];

            return issuedOn > context.Today
                ? SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency,
                    $"Issue date {issuedOn:yyyy-MM-dd} is in the future.")
                : SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency);
        }

        if (string.Equals(name, CertificateFieldNames.Standard, StringComparison.OrdinalIgnoreCase)
            && context.ExpectedStandard is not null)
        {
            var matches = string.Equals(
                TextNormalizer.Normalize(assessment.TypedValue ?? assessment.Candidate!.RawValue),
                TextNormalizer.Normalize(context.ExpectedStandard),
                StringComparison.Ordinal);

            return matches
                ? SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency)
                : SignalOutcome.Fail(
                    ConfidenceSignal.CrossFieldConsistency,
                    $"Certificate states '{assessment.Candidate!.RawValue}' but the requirement asks for '{context.ExpectedStandard}'.");
        }

        return null;
    }

    /// <summary>
    /// Entity match (weight 0.15).
    /// <para>
    /// Deliberately pass/fail at <see cref="NameSimilarity.MatchThreshold"/> rather than graded by
    /// similarity, because SRS §8.3 defines the check as a threshold test — and because grading it
    /// would defeat it. A holder name scoring 0.75 similarity under graded credit costs only 5% of
    /// the final confidence, so "Meridian Logistics Group" on a certificate issued to "Meridian
    /// Logistics SARL" would still sail past a 0.85 auto-accept bar. A different company holding
    /// the certificate is exactly the failure this product exists to catch, so it fails outright
    /// and the measured similarity goes in the detail for the reviewer to judge.
    /// </para>
    /// </summary>
    private static SignalOutcome? ScoreEntityMatch(FieldAssessment assessment, ExtractionContext context)
    {
        var raw = assessment.Candidate!.RawValue;

        switch (assessment.Definition.EntityMatch)
        {
            case EntityMatchTarget.SupplierName:
                var similarity = NameSimilarity.Best(raw, context.SupplierLegalName, context.SupplierTradingName);

                return similarity >= NameSimilarity.MatchThreshold
                    ? SignalOutcome.Pass(
                        ConfidenceSignal.EntityMatch,
                        $"Holder matches supplier '{context.SupplierLegalName}' (similarity {Format(similarity)}).")
                    : SignalOutcome.Fail(
                        ConfidenceSignal.EntityMatch,
                        $"Holder '{raw}' does not match supplier '{context.SupplierLegalName}' (similarity {Format(similarity)}, needs {Format(NameSimilarity.MatchThreshold)}).");

            case EntityMatchTarget.AcceptedIssuer:
                // Only a constraint when the requirement says so (SRS §6.1 RequiresIssuerMatch).
                // Otherwise there is no list to check against and the signal does not apply.
                if (!context.RequiresIssuerMatch)
                {
                    return null;
                }

                var issuerSimilarity = NameSimilarity.BestOf(raw, context.AcceptedIssuers);

                return issuerSimilarity >= NameSimilarity.MatchThreshold
                    ? SignalOutcome.Pass(ConfidenceSignal.EntityMatch)
                    : SignalOutcome.Fail(
                        ConfidenceSignal.EntityMatch,
                        $"Issuer '{raw}' is not among the accepted issuers for this requirement (best similarity {Format(issuerSimilarity)}).");

            case EntityMatchTarget.None:
            default:
                return null;
        }
    }

    /// <summary>
    /// Explicitly invariant. These strings are read by reviewers in both language settings
    /// (NFR-16) and are written verbatim into the audit trail, so the decimal separator must not
    /// depend on the culture of whichever container happened to score the field.
    /// </summary>
    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Per-field state carried between the two passes.</summary>
    private sealed record FieldAssessment(
        FieldDefinition Definition,
        FieldCandidate? Candidate,
        GroundingCheck? GroundingCheck,
        string? TypedValue);
}

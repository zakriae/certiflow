using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// The whole scoring pipeline over a realistic certificate. These are the cases that appear on
/// screen in the SRS §17.1 walkthrough, so each one is written to be the thing a viewer sees.
/// </summary>
public sealed class FieldEvaluatorTests
{
    private static ExtractedField Field(IReadOnlyList<ExtractedField> fields, string name) =>
        fields.Single(f => string.Equals(f.FieldName, name, StringComparison.Ordinal));

    [Fact]
    public void An_honest_extraction_scores_full_confidence_on_every_field()
    {
        var fields = new CertificateFixture().Evaluate();

        fields.Should().HaveCount(7);
        fields.Should().OnlyContain(f => f.Confidence.Value == 1.00m);
        fields.Should().OnlyContain(f => f.GroundingResult == GroundingResult.Verified);
        fields.Should().OnlyContain(f => f.Citation!.IsLocated);
    }

    [Fact]
    public void A_fabricated_expiry_date_scores_zero_and_is_flagged_as_not_in_the_source()
    {
        // The amber-to-red moment in the demo. The model returned a plausible, well-formed,
        // internally consistent date — and it is not in the document.
        var fixture = new CertificateFixture();

        var fields = fixture.Evaluate(fixture.CandidatesWith(
            new FieldCandidate(
                CertificateFieldNames.ExpiresOn,
                "2029-12-31",
                new Citation(2, "Expiry Date: 2029-12-31"))));

        var expiry = Field(fields, CertificateFieldNames.ExpiresOn);
        expiry.Confidence.Should().Be(Confidence.Zero);
        expiry.GroundingResult.Should().Be(GroundingResult.NotFoundInSource);
        expiry.Note.Should().Contain("not found");

        // Every other field is untouched — one bad field does not poison the rest.
        Field(fields, CertificateFieldNames.IssuedOn).Confidence.Value.Should().Be(1.00m);
    }

    [Fact]
    public void A_certificate_issued_to_a_different_legal_entity_falls_below_the_auto_accept_bar()
    {
        // SRS §16.1's deliberately mismatched certificate. The text is genuinely on the document,
        // so grounding passes — it is the entity check that catches this, and it must.
        var fixture = new CertificateFixture { Holder = "Meridian Logistics Group" };

        var holder = Field(fixture.Evaluate(), CertificateFieldNames.HolderName);

        holder.GroundingResult.Should().Be(GroundingResult.Verified);

        // holderName has no cross-field rule, so its available weight is grounding + type +
        // entity = 0.75, of which the entity failure costs 0.15: 0.60 / 0.75 = 0.80.
        holder.Confidence.Value.Should().Be(0.80m);
        holder.MeetsThreshold(Confidence.FromScore(0.85m)).Should().BeFalse();
        holder.Signals.Single(s => s.Signal == ConfidenceSignal.EntityMatch)
            .Detail.Should().Contain("does not match supplier");
    }

    [Fact]
    public void A_date_the_model_could_not_format_loses_the_type_and_consistency_checks()
    {
        var fixture = new CertificateFixture { ExpiresOn = "31/13/2026" };

        var expiry = Field(fixture.Evaluate(), CertificateFieldNames.ExpiresOn);

        // Grounded (0.40) but neither parseable nor checkable: 0.40 of an available 0.80.
        expiry.GroundingResult.Should().Be(GroundingResult.Verified);
        expiry.TypedValue.Should().BeNull();
        expiry.Confidence.Value.Should().Be(0.50m);
    }

    [Fact]
    public void A_french_formatted_date_is_accepted_and_canonicalised_to_iso()
    {
        // The structured-output schema asks for ISO; models do not always comply, and rejecting a
        // correctly-read date over its format would be a false negative charged to the reviewer.
        var fixture = new CertificateFixture { ExpiresOn = "13/03/2027" };

        var expiry = Field(fixture.Evaluate(), CertificateFieldNames.ExpiresOn);

        expiry.TypedValue.Should().Be("2027-03-13");
        expiry.Confidence.Value.Should().Be(1.00m);
    }

    [Fact]
    public void An_expiry_before_its_issue_date_fails_the_consistency_check()
    {
        var fixture = new CertificateFixture { ExpiresOn = "2024-01-31" };

        var expiry = Field(fixture.Evaluate(), CertificateFieldNames.ExpiresOn);

        // Grounded and well-formed, but nonsense: 0.60 of an available 0.80.
        expiry.Confidence.Value.Should().Be(0.75m);
        expiry.Signals.Single(s => s.Signal == ConfidenceSignal.CrossFieldConsistency)
            .Detail.Should().Contain("not after the issue date");
    }

    [Fact]
    public void An_implausibly_long_validity_span_fails_the_consistency_check()
    {
        // Almost always a mis-read year. The same five-year bound BC5's ValidityPeriod enforces.
        var fixture = new CertificateFixture { ExpiresOn = "2045-03-13" };

        Field(fixture.Evaluate(), CertificateFieldNames.ExpiresOn)
            .Signals.Single(s => s.Signal == ConfidenceSignal.CrossFieldConsistency)
            .Detail.Should().Contain("implausibly long");
    }

    [Fact]
    public void An_issue_date_in_the_future_fails_the_consistency_check()
    {
        var fixture = new CertificateFixture { IssuedOn = "2027-01-01", ExpiresOn = "2028-01-01" };

        Field(fixture.Evaluate(), CertificateFieldNames.IssuedOn)
            .Signals.Single(s => s.Signal == ConfidenceSignal.CrossFieldConsistency)
            .Detail.Should().Contain("in the future");
    }

    [Fact]
    public void A_standard_that_does_not_match_the_requirement_fails_consistency()
    {
        var fixture = new CertificateFixture { Standard = "ISO 14001:2015" };

        var standard = Field(fixture.Evaluate(), CertificateFieldNames.Standard);

        standard.TypedValue.Should().Be("ISO 14001:2015", "it is a valid standard, just the wrong one");
        standard.Signals.Single(s => s.Signal == ConfidenceSignal.CrossFieldConsistency)
            .Detail.Should().Contain("asks for 'ISO 9001:2015'");
    }

    [Fact]
    public void An_unrecognised_issuer_fails_only_when_the_requirement_constrains_issuers()
    {
        var fixture = new CertificateFixture { Issuer = "Backstreet Certification Ltd" };

        var constrained = Field(fixture.Evaluate(requiresIssuerMatch: true), CertificateFieldNames.IssuerName);
        var unconstrained = Field(fixture.Evaluate(requiresIssuerMatch: false), CertificateFieldNames.IssuerName);

        constrained.Confidence.Value.Should().Be(0.80m);
        unconstrained.Confidence.Value.Should().Be(1.00m, "with no accepted-issuer list there is nothing to check");
    }

    [Fact]
    public void A_field_with_no_citation_scores_zero_however_correct_the_value_is()
    {
        // The value below is exactly right. Without provenance it is still worth nothing (FR-3.3).
        var fixture = new CertificateFixture();

        var expiry = Field(
            fixture.Evaluate(fixture.CandidatesWith(
                new FieldCandidate(CertificateFieldNames.ExpiresOn, "2027-03-13", Citation: null))),
            CertificateFieldNames.ExpiresOn);

        expiry.Confidence.Should().Be(Confidence.Zero);
        expiry.GroundingResult.Should().Be(GroundingResult.NotAttempted);
    }

    [Fact]
    public void A_mandatory_field_the_model_skipped_is_reported_rather_than_omitted()
    {
        var fixture = new CertificateFixture();
        var withoutNumber = fixture.Candidates()
            .Where(c => c.FieldName != CertificateFieldNames.CertificateNumber)
            .ToList();

        var fields = fixture.Evaluate(withoutNumber);

        var number = Field(fields, CertificateFieldNames.CertificateNumber);
        number.RawValue.Should().BeNull();
        number.Confidence.Should().Be(Confidence.Zero);
        number.Note.Should().Contain("no value");
    }

    [Fact]
    public void Candidates_the_schema_does_not_declare_are_ignored()
    {
        // A model returning extra keys must not be able to widen the extraction contract.
        var fixture = new CertificateFixture();
        var withExtra = fixture.Candidates()
            .Append(new FieldCandidate("auditorSignature", "J. Dupont", new Citation(1, "CERTIFICATE OF REGISTRATION")))
            .ToList();

        fixture.Evaluate(withExtra).Should().HaveCount(7)
            .And.NotContain(f => f.FieldName == "auditorSignature");
    }

    [Fact]
    public void A_second_pass_that_disagrees_costs_the_model_agreement_signal()
    {
        var fixture = new CertificateFixture();

        var expiry = Field(
            fixture.Evaluate(fixture.CandidatesWith(
                new FieldCandidate(
                    CertificateFieldNames.ExpiresOn,
                    "2027-03-13",
                    new Citation(2, "Expiry Date: 2027-03-13"),
                    SecondPassValue: "2027-03-31"))),
            CertificateFieldNames.ExpiresOn);

        // 0.80 of an available 0.85 once model agreement is in play and fails.
        expiry.Confidence.Value.Should().Be(0.94m);
        expiry.Signals.Should().Contain(s => s.Signal == ConfidenceSignal.ModelAgreement && s.Score == 0m);
    }
}

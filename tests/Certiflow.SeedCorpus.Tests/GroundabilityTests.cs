using Certiflow.Intelligence.Domain.Grounding;
using FluentAssertions;
using Xunit;

namespace Certiflow.SeedCorpus.Tests;

/// <summary>
/// <b>The test that makes the corpus fit for purpose.</b>
/// <para>
/// A synthetic certificate that looks right to a human but whose text layer cannot be searched is
/// worse than useless: grounding fails, confidence is vetoed to zero, and every seeded document
/// lands in the review queue. That would look exactly like a broken extraction pipeline while
/// actually being a broken corpus, and it would be found during a recording session rather than
/// here.
/// </para>
/// </summary>
public sealed class GroundabilityTests(GeneratedCorpusFixture corpus) : IClassFixture<GeneratedCorpusFixture>
{
    [Fact]
    public void Every_certificate_produces_a_readable_text_layer()
    {
        foreach (var certificate in corpus.Certificates)
        {
            var parsed = corpus.Parsed[certificate.DocumentId];

            parsed.IsEmpty.Should().BeFalse(
                "{0} must have a text layer the pipeline can read", certificate.FileName);
        }
    }

    [Fact]
    public void Every_extractable_field_value_is_locatable_by_the_real_grounding_verifier()
    {
        var failures = new List<string>();

        foreach (var certificate in corpus.Certificates)
        {
            var parsed = corpus.Parsed[certificate.DocumentId];

            foreach (var (field, value) in CorpusGenerator.ExtractableValues(certificate))
            {
                // Exactly what BC3 does with a model's claimed citation: build one and try to
                // locate it in the parsed source text.
                var citation = new Citation(pageNumber: 1, snippet: value);
                var check = GroundingVerifier.Verify(citation, parsed);

                if (!check.IsGrounded)
                {
                    failures.Add($"{certificate.FileName} · {field} · \"{value}\" · {check.Detail}");
                }
            }
        }

        failures.Should().BeEmpty(
            "every seeded value must be groundable, otherwise confidence is vetoed to zero for a "
            + "document that is entirely correct");
    }

    [Fact]
    public void Formatted_dates_are_locatable_in_both_languages()
    {
        // Dates are the field a reviewer cares about most and the one most easily broken by
        // formatting: a French certificate reading "14 septembre 2027" has to be findable too.
        var failures = new List<string>();

        foreach (var certificate in corpus.Certificates)
        {
            var parsed = corpus.Parsed[certificate.DocumentId];
            var culture = certificate.Language == CorpusLanguage.French
                ? new System.Globalization.CultureInfo("fr-FR")
                : new System.Globalization.CultureInfo("en-GB");

            foreach (var (field, date) in new[]
                     {
                         (CertificateFields.IssuedOn, certificate.IssuedOn),
                         (CertificateFields.ExpiresOn, certificate.ExpiresOn),
                     })
            {
                var rendered = date.ToString("d MMMM yyyy", culture);
                var check = GroundingVerifier.Verify(new Citation(1, rendered), parsed);

                if (!check.IsGrounded)
                {
                    failures.Add($"{certificate.FileName} · {field} · \"{rendered}\"");
                }
            }
        }

        failures.Should().BeEmpty("both dates on every certificate must be locatable as written");
    }

    [Fact]
    public void A_value_that_is_not_on_the_certificate_is_correctly_reported_as_ungrounded()
    {
        // The negative control. Without it, the tests above would pass just as happily against a
        // verifier that returned Verified for everything.
        var certificate = corpus.Certificates.First();
        var parsed = corpus.Parsed[certificate.DocumentId];

        var check = GroundingVerifier.Verify(new Citation(1, "Certificate No. ZZ-FAKE-000000"), parsed);

        check.IsGrounded.Should().BeFalse();
        check.Result.Should().Be(GroundingResult.NotFoundInSource);
    }

    [Fact]
    public void French_certificates_ground_despite_accented_characters()
    {
        var french = corpus.Certificates.First(c => c.Language == CorpusLanguage.French);
        var parsed = corpus.Parsed[french.DocumentId];

        // "Valable jusqu'au" carries both an accent-adjacent apostrophe and French month names -
        // the exact case TextNormalizer exists for.
        GroundingVerifier.Verify(new Citation(1, french.IssuerName), parsed).IsGrounded.Should().BeTrue();
        GroundingVerifier.Verify(new Citation(1, french.Scope), parsed).IsGrounded.Should().BeTrue();
    }

    [Fact]
    public void The_deliberately_mismatched_certificate_still_grounds_but_names_the_wrong_holder()
    {
        // The entity-match demo case. The document is honest about what it says; the point is that
        // what it says does not match the supplier it was filed against, and grounding must not
        // hide that by failing for an unrelated reason.
        var supplier = corpus.Manifest.Suppliers.Single(s => s.LegalName == "Sterling Site Services");
        var certificate = supplier.Certificates.Single(c => c.DemoNote is not null);
        var parsed = corpus.Parsed[certificate.DocumentId];

        certificate.HolderName.Should().NotBe(supplier.LegalName);
        GroundingVerifier.Verify(new Citation(1, certificate.HolderName), parsed).IsGrounded.Should().BeTrue();
    }
}

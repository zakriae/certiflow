using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// Regression tests for a false negative found by running the real pipeline against a real French
/// certificate (SRS §22.3).
/// <para>
/// The prompt asks the model to normalise dates to ISO, but a model that ignores that and returns
/// <c>26 septembre 2027</c> has still <em>read the certificate correctly</em>. Scoring it as a type
/// failure costs the field 0.20, and because overall confidence is the worst mandatory field, it
/// drags the whole document below the auto-accept threshold — charging a reviewer for the model's
/// formatting preference rather than for anything wrong with the certificate.
/// </para>
/// </summary>
public sealed class LongFormDateTests
{
    private static ExtractedField Evaluate(string printedDate)
    {
        var fixture = new CertificateFixture();

        var candidates = fixture.CandidatesWith(
            new FieldCandidate(
                CertificateFieldNames.ExpiresOn,
                printedDate,
                // The citation still points at real text on the page, so grounding passes and the
                // only thing under test is whether the value could be typed.
                new Citation(2, $"Expiry Date: {fixture.ExpiresOn}")));

        return fixture.Evaluate(candidates)
            .Single(field => field.FieldName == CertificateFieldNames.ExpiresOn);
    }

    [Theory]
    [InlineData("2027-03-13")]        // ISO, what the prompt asks for
    [InlineData("13/03/2027")]        // numeric, as French certificates often print it
    [InlineData("13 March 2027")]     // English long form
    [InlineData("13 mars 2027")]      // French long form — the case found in production
    [InlineData("March 13, 2027")]    // US long form
    public void A_correctly_read_date_types_successfully_however_it_was_printed(string printed)
    {
        var field = Evaluate(printed);

        field.TypedValue.Should().Be("2027-03-13", "the value normalises to ISO whatever its input format");
        field.Confidence.Value.Should().Be(1.00m);
    }

    [Theory]
    [InlineData("sometime in March")]
    [InlineData("13 marchz 2027")]
    [InlineData("2027-13-45")]
    public void Something_that_is_not_a_date_still_fails(string printed)
    {
        // The negative control. Widening the accepted formats must not widen them to everything.
        var field = Evaluate(printed);

        field.TypedValue.Should().BeNull();
        field.Confidence.Value.Should().BeLessThan(1.00m);
    }

    [Fact]
    public void A_french_date_does_not_drag_the_document_below_the_auto_accept_threshold()
    {
        // The actual symptom: one long-form date scored 0.50 and took overall confidence with it,
        // so a perfectly good certificate was not auto-acceptable.
        var fixture = new CertificateFixture();

        var candidates = fixture.CandidatesWith(
            new FieldCandidate(
                CertificateFieldNames.ExpiresOn,
                "13 mars 2027",
                new Citation(2, $"Expiry Date: {fixture.ExpiresOn}")));

        var worstMandatory = fixture.Evaluate(candidates)
            .Where(field => field.IsMandatory)
            .Min(field => field.Confidence.Value);

        worstMandatory.Should().BeGreaterThanOrEqualTo(0.85m);
    }
}

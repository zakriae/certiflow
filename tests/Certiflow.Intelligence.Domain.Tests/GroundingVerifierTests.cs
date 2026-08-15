using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// SRS §19 Q3 — "what stops the AI hallucinating a date?" These tests are the answer.
/// </summary>
public sealed class GroundingVerifierTests
{
    private static ParsedDocument Certificate() => new(
        [
            new DocumentPage(1, "CERTIFICAT ISO 9001:2015\nDélivré à : Meridian Logistics SARL\nNuméro : FR-9001-00417"),
            new DocumentPage(2, "Date d'émission : 2025-03-14\nValable jusqu'au : 2027-03-13\nOrganisme : AFNOR Certification"),
        ],
        TextSource.EmbeddedTextLayer);

    [Fact]
    public void A_snippet_present_on_the_cited_page_is_verified_with_offsets()
    {
        var citation = new Citation(2, "Valable jusqu'au : 2027-03-13");

        var result = GroundingVerifier.Verify(citation, Certificate());

        result.Result.Should().Be(GroundingResult.Verified);
        result.PageMismatch.Should().BeFalse();
        result.Citation!.IsLocated.Should().BeTrue();
        result.Citation.CharOffsetEnd.Should().BeGreaterThan(result.Citation.CharOffsetStart!.Value);
    }

    [Fact]
    public void Verification_survives_casing_accents_and_line_breaks()
    {
        // The model returns clean prose; the PDF text layer does not. Both must ground.
        var citation = new Citation(2, "DATE D'ÉMISSION : 2025-03-14");

        GroundingVerifier.Verify(citation, Certificate()).Result
            .Should().Be(GroundingResult.Verified);
    }

    [Fact]
    public void A_fabricated_expiry_date_is_caught()
    {
        // The invented value forces the model to invent the sentence containing it — and an
        // invented sentence is not in the PDF. This is the whole mechanism.
        var citation = new Citation(2, "Valable jusqu'au : 2029-12-31");

        var result = GroundingVerifier.Verify(citation, Certificate());

        result.Result.Should().Be(GroundingResult.NotFoundInSource);
        result.IsGrounded.Should().BeFalse();
        result.Detail.Should().Contain("not found");
    }

    [Fact]
    public void Text_found_on_a_different_page_is_still_grounded_but_flagged_and_relocated()
    {
        // Models get the value right and the page number wrong. That is a page error, not a
        // fabrication — treating it as one would send correct extractions to a reviewer.
        var citation = new Citation(1, "Organisme : AFNOR Certification");

        var result = GroundingVerifier.Verify(citation, Certificate());

        result.Result.Should().Be(GroundingResult.Verified);
        result.PageMismatch.Should().BeTrue();
        result.Citation!.PageNumber.Should().Be(2, "the preview must jump to the page that has the text");
        result.Detail.Should().Contain("page 2");
    }

    [Fact]
    public void A_citation_to_a_page_that_does_not_exist_is_reported_clearly()
    {
        var citation = new Citation(9, "Numéro : FR-9001-00417");

        var result = GroundingVerifier.Verify(citation, Certificate());

        // The text does exist, on page 1 — so this grounds, with the page corrected.
        result.Result.Should().Be(GroundingResult.Verified);
        result.Citation!.PageNumber.Should().Be(1);
    }

    [Fact]
    public void A_nonexistent_page_and_nonexistent_text_reports_the_page_problem()
    {
        var citation = new Citation(9, "Numéro : FR-0000-99999");

        var result = GroundingVerifier.Verify(citation, Certificate());

        result.Result.Should().Be(GroundingResult.NotFoundInSource);
        result.Detail.Should().Contain("does not exist").And.Contain("2 page");
    }

    [Fact]
    public void No_citation_at_all_is_not_attempted_rather_than_a_failure()
    {
        var result = GroundingVerifier.Verify(citation: null, Certificate());

        result.Result.Should().Be(GroundingResult.NotAttempted);
        result.IsGrounded.Should().BeFalse();
    }

    [Fact]
    public void A_snippet_too_short_to_be_distinctive_is_refused_outright()
    {
        // "2026" appears on every page of a certificate. Grounding it proves nothing, so it may
        // not earn the 0.40 grounding weight.
        var act = () => new Citation(1, "2026");

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.citation.snippet_too_short");
    }

    [Fact]
    public void A_document_of_scans_with_no_text_layer_is_reported_as_empty()
    {
        var scanned = new ParsedDocument(
            [new DocumentPage(1, string.Empty), new DocumentPage(2, "  ")],
            TextSource.EmbeddedTextLayer);

        scanned.IsEmpty.Should().BeTrue("the pipeline must branch to OCR (FR-3.6)");
    }
}

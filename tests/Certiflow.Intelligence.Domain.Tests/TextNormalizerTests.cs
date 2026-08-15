using Certiflow.Intelligence.Domain.Grounding;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// Every case here is a false negative that grounding would otherwise produce — a correct
/// extraction sent to a human because a PDF renders a character differently than a model does.
/// </summary>
public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("Certificat d'Enregistrement", "certificat d'enregistrement")]
    [InlineData("ORGANISME CERTIFICATEUR", "organisme certificateur")]
    public void Casefolds(string input, string expected) =>
        TextNormalizer.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Société Générale de Contrôle", "societe generale de controle")]
    [InlineData("Bâtiment & Génie Civil", "batiment & genie civil")]
    [InlineData("ÉTABLISSEMENT", "etablissement")]
    public void Folds_french_diacritics(string input, string expected) =>
        TextNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Collapses_line_breaks_and_repeated_whitespace()
    {
        // PDF text layers break lines mid-phrase; the model returns the phrase unbroken.
        TextNormalizer.Normalize("valid  until\n\t 31 December   2026")
            .Should().Be("valid until 31 december 2026");
    }

    [Fact]
    public void Canonicalises_dashes_so_certificate_numbers_match()
    {
        // An en dash in the PDF and a hyphen from the model are the same certificate number.
        TextNormalizer.Normalize("FR–9001–2026").Should().Be(TextNormalizer.Normalize("FR-9001-2026"));
    }

    [Fact]
    public void Canonicalises_curly_apostrophes()
    {
        TextNormalizer.Normalize("l’organisme").Should().Be(TextNormalizer.Normalize("l'organisme"));
    }

    [Fact]
    public void Expands_ligatures_emitted_by_pdf_producers()
    {
        TextNormalizer.Normalize("certiﬁcate").Should().Be("certificate");
    }

    [Fact]
    public void Treats_non_breaking_and_zero_width_spaces_as_ordinary_text()
    {
        TextNormalizer.Normalize("ISO 9001").Should().Be("iso 9001");
        TextNormalizer.Normalize("ISO​9001").Should().Be("iso9001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n ")]
    public void Returns_empty_for_nothing_useful(string? input) =>
        TextNormalizer.Normalize(input).Should().BeEmpty();

    [Fact]
    public void Preserves_digits_and_their_order()
    {
        // Nothing that could change which certificate a snippet refers to may be dropped.
        TextNormalizer.Normalize("No. 2026/00417-B").Should().Contain("2026/00417-b");
    }
}

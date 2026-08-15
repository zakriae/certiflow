using Certiflow.Intelligence.Domain.Scoring;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// The entity-match check of SRS §8.3. The interesting tests are the ones that must <em>fail</em>:
/// a name check that waves through a different company is worse than no check, because it puts a
/// green tick on the exact failure the product exists to catch.
/// </summary>
public sealed class NameSimilarityTests
{
    private const string OnRecord = "Meridian Logistics SARL";

    [Theory]
    [InlineData("Meridian Logistics SARL")]
    [InlineData("MERIDIAN LOGISTICS SARL")]
    [InlineData("Meridian Logistics")]
    [InlineData("Meridian Logistics S.A.R.L.")]
    public void Matches_the_same_company_however_the_legal_form_is_printed(string candidate) =>
        NameSimilarity.IsMatch(candidate, OnRecord).Should().BeTrue();

    [Fact]
    public void Tolerates_a_typo_or_an_ocr_slip()
    {
        // "Logistic" for "Logistics" is a scanning artefact, not a different company.
        NameSimilarity.IsMatch("Meridian Logistic", OnRecord).Should().BeTrue();
    }

    [Fact]
    public void Folds_diacritics_so_french_names_match_their_ascii_spelling()
    {
        NameSimilarity.IsMatch("Societe Generale de Controle", "Société Générale de Contrôle")
            .Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_different_legal_entity_in_the_same_corporate_family()
    {
        // This is the seed corpus's deliberately mismatched certificate (SRS §16.1). "Group" is a
        // different entity, and no amount of shared prefix should make this a match.
        NameSimilarity.IsMatch("Meridian Logistics Group", OnRecord).Should().BeFalse();
    }

    [Fact]
    public void Refuses_an_unrelated_company()
    {
        NameSimilarity.Score("Acme Transport", OnRecord)
            .Should().BeLessThan(NameSimilarity.MatchThreshold);
    }

    [Fact]
    public void Matches_against_a_trading_name_when_the_legal_name_does_not_match()
    {
        // A certificate may legitimately be issued to the trading name.
        NameSimilarity.IsMatch("Meridian Freight", OnRecord, "Meridian Freight")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Scores_nothing_against_a_missing_candidate(string? candidate) =>
        NameSimilarity.Score(candidate, OnRecord).Should().Be(0m);

    [Fact]
    public void A_name_that_is_nothing_but_a_legal_form_does_not_match_everything()
    {
        // Stripping every token would leave an empty string, which would otherwise be "contained
        // in" any name at all.
        NameSimilarity.IsMatch("SARL", OnRecord).Should().BeFalse();
    }

    [Fact]
    public void Scoring_against_an_empty_issuer_list_yields_zero()
    {
        NameSimilarity.BestOf("AFNOR Certification", []).Should().Be(0m);
    }
}

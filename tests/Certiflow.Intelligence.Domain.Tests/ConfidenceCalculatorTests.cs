using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

/// <summary>
/// SRS §19 Q4 — "why not trust the model's own confidence?" Note that no test in this file
/// mentions a model: confidence is a function of check outcomes, and nothing else.
/// </summary>
public sealed class ConfidenceCalculatorTests
{
    [Fact]
    public void Every_check_passing_scores_one()
    {
        var breakdown = ConfidenceCalculator.Compute(
        [
            SignalOutcome.Pass(ConfidenceSignal.Grounding),
            SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
            SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency),
            SignalOutcome.Pass(ConfidenceSignal.EntityMatch),
            SignalOutcome.Pass(ConfidenceSignal.ModelAgreement),
        ]);

        breakdown.Confidence.Value.Should().Be(1.00m);
        breakdown.GroundingVetoed.Should().BeFalse();
    }

    [Fact]
    public void A_failed_grounding_check_forces_zero_however_well_everything_else_scored()
    {
        // The critical property. Losing only the 0.40 grounding weight would leave 0.60 — and a
        // fabricated value that happens to be a well-formed, internally consistent date belonging
        // to the right company would clear a 0.85 bar on a different weighting. It must not.
        var breakdown = ConfidenceCalculator.Compute(
        [
            SignalOutcome.Fail(ConfidenceSignal.Grounding, "Snippet not present in the document."),
            SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
            SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency),
            SignalOutcome.Pass(ConfidenceSignal.EntityMatch),
            SignalOutcome.Pass(ConfidenceSignal.ModelAgreement),
        ]);

        breakdown.Confidence.Should().Be(Confidence.Zero);
        breakdown.GroundingVetoed.Should().BeTrue();
        breakdown.VetoReason.Should().Be("Snippet not present in the document.");
    }

    [Fact]
    public void A_field_with_no_grounding_check_at_all_scores_zero()
    {
        // Grounding is not optional (FR-3.4). An unverified field is an untrusted field.
        var breakdown = ConfidenceCalculator.Compute(
        [
            SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
            SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency),
        ]);

        breakdown.Confidence.Should().Be(Confidence.Zero);
        breakdown.GroundingVetoed.Should().BeTrue();
        breakdown.VetoReason.Should().Contain("No grounding check");
    }

    [Fact]
    public void An_unevaluated_optional_signal_is_renormalised_away_not_counted_as_a_failure()
    {
        // Model agreement (FR-3.10) is a Could. If it never runs, a perfect field must still score
        // 1.00 — a check that costs 5% for not existing produces unexplained review volume.
        var breakdown = ConfidenceCalculator.Compute(
        [
            SignalOutcome.Pass(ConfidenceSignal.Grounding),
            SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
            SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency),
            SignalOutcome.Pass(ConfidenceSignal.EntityMatch),
        ]);

        breakdown.Confidence.Value.Should().Be(1.00m);
        breakdown.Signals.Should().HaveCount(4);
    }

    [Fact]
    public void Weights_are_applied_over_the_signals_that_actually_ran()
    {
        // Grounded but unparseable: 0.40 of an available 0.60 → 0.666..., truncated to 0.66.
        var breakdown = ConfidenceCalculator.Compute(
        [
            SignalOutcome.Pass(ConfidenceSignal.Grounding),
            SignalOutcome.Fail(ConfidenceSignal.TypeValidity, "'31/13/2026' is not a date."),
        ]);

        breakdown.Confidence.Value.Should().Be(0.66m);
    }

    [Fact]
    public void A_failed_entity_match_drops_a_field_below_the_default_auto_accept_bar()
    {
        // A mismatched holder name has to cost enough to send the document to a reviewer, on any
        // combination of signals. Here: 0.80 of an available 0.95.
        var breakdown = ConfidenceCalculator.Compute(
        [
            SignalOutcome.Pass(ConfidenceSignal.Grounding),
            SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
            SignalOutcome.Pass(ConfidenceSignal.CrossFieldConsistency),
            SignalOutcome.Fail(ConfidenceSignal.EntityMatch, "Holder does not match supplier."),
        ]);

        breakdown.Confidence.Value.Should().Be(0.84m);
        breakdown.Confidence.MeetsOrExceeds(Confidence.FromScore(0.85m)).Should().BeFalse();
    }

    [Fact]
    public void The_signal_weights_are_the_srs_table_and_sum_to_one()
    {
        // Pinned deliberately: these numbers get quoted on camera (SRS §17.1) and a silent change
        // here would change every auto-accept decision in the system.
        ConfidenceCalculator.Weights[ConfidenceSignal.Grounding].Should().Be(0.40m);
        ConfidenceCalculator.Weights[ConfidenceSignal.TypeValidity].Should().Be(0.20m);
        ConfidenceCalculator.Weights[ConfidenceSignal.CrossFieldConsistency].Should().Be(0.20m);
        ConfidenceCalculator.Weights[ConfidenceSignal.EntityMatch].Should().Be(0.15m);
        ConfidenceCalculator.Weights[ConfidenceSignal.ModelAgreement].Should().Be(0.05m);
        ConfidenceCalculator.Weights.Values.Sum().Should().Be(1.00m);
    }

    [Fact]
    public void Reporting_the_same_signal_twice_is_a_programming_error()
    {
        var act = () => ConfidenceCalculator.Compute(
        [
            SignalOutcome.Pass(ConfidenceSignal.Grounding),
            SignalOutcome.Fail(ConfidenceSignal.Grounding),
        ]);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.confidence.duplicate_signal");
    }

    [Theory]
    [InlineData(0.8499, 0.84)]
    [InlineData(0.8500, 0.85)]
    [InlineData(0.8599, 0.85)]
    [InlineData(0.9999, 0.99)]
    public void Scores_round_toward_zero_so_rounding_never_creates_an_auto_accept(decimal raw, decimal expected)
    {
        // 0.8499 becoming 0.85 would auto-accept a field the checks did not clear. In a compliance
        // product the rounding error has to fall on the side of asking a human.
        Confidence.FromScore(raw).Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void A_score_outside_zero_to_one_is_refused(decimal raw)
    {
        var act = () => Confidence.FromScore(raw);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.confidence.out_of_range");
    }
}

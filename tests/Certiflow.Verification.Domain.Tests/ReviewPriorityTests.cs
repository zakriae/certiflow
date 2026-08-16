using FluentAssertions;
using Xunit;

using static Certiflow.Verification.Domain.Tests.ReviewScenario;

namespace Certiflow.Verification.Domain.Tests;

/// <summary>
/// FR-4.8 — queue priority by expiry proximity. Derived from a date rather than stored, because a
/// queue whose priorities are written once and left is wrong by the next morning.
/// </summary>
public sealed class ReviewPriorityTests
{
    [Theory]
    [InlineData(-1, ReviewPriority.Critical)]
    [InlineData(0, ReviewPriority.Critical)]
    [InlineData(7, ReviewPriority.Critical)]
    [InlineData(8, ReviewPriority.High)]
    [InlineData(30, ReviewPriority.High)]
    [InlineData(31, ReviewPriority.Normal)]
    [InlineData(90, ReviewPriority.Normal)]
    [InlineData(91, ReviewPriority.Low)]
    public void Priority_tracks_how_close_the_current_evidence_is_to_lapsing(
        int daysUntilExpiry,
        ReviewPriority expected)
    {
        var task = Open(currentEvidenceExpiresOn: Today.AddDays(daysUntilExpiry));

        task.PriorityOn(Today).Should().Be(expected);
    }

    [Fact]
    public void A_requirement_with_nothing_in_force_is_high_but_not_critical()
    {
        // The supplier is already missing this requirement, which is urgent — but less urgent than
        // evidence actively lapsing, because that one has a deadline attached.
        var task = Open(currentEvidenceExpiresOn: null);

        task.PriorityOn(Today).Should().Be(ReviewPriority.High);
    }

    [Fact]
    public void Priority_rises_on_its_own_as_the_expiry_date_approaches()
    {
        // The same task, unchanged, read on two different days.
        var task = Open(currentEvidenceExpiresOn: Today.AddDays(40));

        task.PriorityOn(Today).Should().Be(ReviewPriority.Normal);
        task.PriorityOn(Today.AddDays(15)).Should().Be(ReviewPriority.High);
        task.PriorityOn(Today.AddDays(35)).Should().Be(ReviewPriority.Critical);
    }
}

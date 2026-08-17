using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Compliance.Domain.Tests;

public sealed class ValidityPeriodTests
{
    private static readonly DateOnly Issued = new(2026, 1, 1);

    [Fact]
    public void Is_valid_on_its_first_and_last_day()
    {
        var period = new ValidityPeriod(Issued, Issued.AddDays(10));

        period.IsValidOn(Issued).Should().BeTrue("a certificate is valid on the day it is issued");
        period.IsValidOn(Issued.AddDays(10)).Should().BeTrue("expiry day is inclusive");
    }

    [Fact]
    public void Is_not_valid_outside_its_bounds()
    {
        var period = new ValidityPeriod(Issued, Issued.AddDays(10));

        period.IsValidOn(Issued.AddDays(-1)).Should().BeFalse();
        period.IsValidOn(Issued.AddDays(11)).Should().BeFalse();
    }

    [Fact]
    public void Days_remaining_goes_negative_after_expiry()
    {
        var period = new ValidityPeriod(Issued, Issued.AddDays(10));

        period.DaysRemaining(Issued).Should().Be(10);
        period.DaysRemaining(Issued.AddDays(10)).Should().Be(0);
        period.DaysRemaining(Issued.AddDays(15)).Should().Be(-5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cannot_expire_on_or_before_its_issue_date(int offsetDays)
    {
        var act = () => new ValidityPeriod(Issued, Issued.AddDays(offsetDays));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("compliance.validity.expires_after_issued");
    }

    [Fact]
    public void Rejects_an_implausibly_long_validity_span()
    {
        // A "20-year" certificate is almost always a mis-extracted year, and the same bound is
        // what BC3's cross-field consistency check leans on (SRS §8.4).
        var act = () => new ValidityPeriod(Issued, Issued.AddYears(20));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("compliance.validity.implausible_span");
    }

    [Fact]
    public void Two_periods_with_the_same_dates_are_equal()
    {
        // Structural equality is the reason value objects are records here — no Equals override.
        new ValidityPeriod(Issued, Issued.AddDays(30))
            .Should().Be(new ValidityPeriod(Issued, Issued.AddDays(30)));
    }
}

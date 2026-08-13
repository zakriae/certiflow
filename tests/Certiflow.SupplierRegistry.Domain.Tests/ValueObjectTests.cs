using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.SupplierRegistry.Domain.Tests;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("amine@meridian-logistics.demo")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    public void Accepts_a_deliverable_looking_address(string value) =>
        EmailAddress.Parse(value).Value.Should().Be(value.ToLowerInvariant());

    [Fact]
    public void Normalises_case_so_the_same_person_is_not_added_twice()
    {
        EmailAddress.Parse("Amine@Meridian-Logistics.Demo")
            .Should().Be(EmailAddress.Parse("amine@meridian-logistics.demo"));
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("two@at@example.com")]
    [InlineData("trailing@")]
    [InlineData("spaces in@example.com")]
    public void Refuses_an_address_that_is_the_wrong_shape(string value)
    {
        var act = () => EmailAddress.Parse(value);

        act.Should().Throw<DomainRuleViolationException>();
    }

    [Theory]
    [InlineData("user@nodot")]
    [InlineData("user@.example.com")]
    [InlineData("user@example.com.")]
    public void Refuses_an_address_with_an_implausible_domain(string value)
    {
        var act = () => EmailAddress.Parse(value);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.email.invalid_domain");
    }
}

public sealed class RegistrationNumberTests
{
    [Fact]
    public void Normalises_separators_and_case_so_uniqueness_actually_works()
    {
        // SRS §6.1 requires uniqueness per country. It is worthless if "FR 812 345 678" and
        // "fr812345678" count as two different companies.
        var a = RegistrationNumber.Parse("FR 812 345 678");
        var b = RegistrationNumber.Parse("fr-812-345-678");

        a.IsSameAs(b).Should().BeTrue();
        a.Normalized.Should().Be("FR812345678");
    }

    [Fact]
    public void Keeps_the_original_for_display()
    {
        RegistrationNumber.Parse("FR 812 345 678").Value.Should().Be("FR 812 345 678");
    }

    [Fact]
    public void Refuses_something_too_short_to_be_a_registration_number()
    {
        var act = () => RegistrationNumber.Parse("--1--");

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.registration_number.too_short");
    }
}

public sealed class CountryCodeTests
{
    [Theory]
    [InlineData("fr", "FR")]
    [InlineData("MA", "MA")]
    [InlineData("be", "BE")]
    public void Uppercases_a_two_letter_code(string input, string expected) =>
        CountryCode.Parse(input).Value.Should().Be(expected);

    [Theory]
    [InlineData("FRA")]
    [InlineData("F")]
    [InlineData("F1")]
    public void Refuses_anything_that_is_not_two_letters(string value)
    {
        var act = () => CountryCode.Parse(value);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.country.invalid");
    }
}

public sealed class DocumentTypeTests
{
    [Fact]
    public void Compares_case_insensitively_because_it_keys_the_extraction_schema()
    {
        DocumentType.Parse("ISO 9001").IsSameAs(DocumentType.Parse("iso 9001")).Should().BeTrue();
    }

    [Fact]
    public void Preserves_the_casing_it_was_given()
    {
        DocumentType.Parse("ISO 9001").Value.Should().Be("ISO 9001");
    }
}

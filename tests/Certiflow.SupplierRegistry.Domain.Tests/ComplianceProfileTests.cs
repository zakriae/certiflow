using Certiflow.SharedKernel;
using Certiflow.SupplierRegistry.Domain.Events;
using FluentAssertions;
using Xunit;

namespace Certiflow.SupplierRegistry.Domain.Tests;

public sealed class ComplianceProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static ComplianceProfile LogisticsProfile()
    {
        var profile = ComplianceProfile.Create("Logistics Contractor");
        profile.AddRequirement(DocumentType.Parse("ISO 9001"), renewalLeadTimeDays: 60);
        profile.AddRequirement(
            DocumentType.Parse("Public Liability Insurance"),
            renewalLeadTimeDays: 30,
            minValidityDays: 30,
            requiresIssuerMatch: true,
            acceptedIssuers: ["AXA", "Allianz", "Generali"]);
        return profile;
    }

    [Fact]
    public void A_new_profile_is_unpublished()
    {
        var profile = ComplianceProfile.Create("Logistics Contractor");

        profile.PublishedVersion.Should().Be(0);
        profile.PublishedAt.Should().BeNull();
    }

    [Fact]
    public void Adding_a_requirement_marks_the_profile_as_having_unpublished_changes()
    {
        // Editing must not silently change what suppliers are judged against — publishing does that,
        // deliberately and traceably.
        var profile = LogisticsProfile();

        profile.HasUnpublishedChanges.Should().BeTrue();
        profile.PublishedVersion.Should().Be(0);
    }

    [Fact]
    public void A_requirement_defaults_to_the_srs_auto_accept_threshold()
    {
        var profile = ComplianceProfile.Create("Logistics Contractor");

        var requirement = profile.AddRequirement(DocumentType.Parse("ISO 9001"));

        requirement.AutoAcceptThreshold.Should().Be(0.85m);
    }

    [Fact]
    public void The_same_document_type_cannot_be_required_twice()
    {
        // Two obligations one certificate could satisfy, with no way to say which it satisfied.
        var profile = LogisticsProfile();

        var act = () => profile.AddRequirement(DocumentType.Parse("iso 9001"));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.profile.duplicate_document_type");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void A_renewal_lead_time_outside_one_to_a_year_is_refused(int days)
    {
        // Zero would mean "warn me the day it expires", which defeats the purpose of a lead time.
        var profile = ComplianceProfile.Create("Logistics Contractor");

        var act = () => profile.AddRequirement(DocumentType.Parse("ISO 9001"), renewalLeadTimeDays: days);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.requirement.lead_time_out_of_range");
    }

    [Fact]
    public void Demanding_an_issuer_match_with_no_accepted_issuers_is_refused()
    {
        var profile = ComplianceProfile.Create("Logistics Contractor");

        var act = () => profile.AddRequirement(
            DocumentType.Parse("Public Liability Insurance"), requiresIssuerMatch: true);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.requirement.issuer_match_without_issuers");
    }

    [Fact]
    public void Publishing_carries_the_whole_requirement_set_so_compliance_never_queries_this_service()
    {
        var profile = LogisticsProfile();

        profile.Publish(Now);

        profile.PublishedVersion.Should().Be(1);
        profile.HasUnpublishedChanges.Should().BeFalse();

        var published = profile.DomainEvents.OfType<ComplianceProfileVersionPublished>()
            .Should().ContainSingle().Subject;

        published.Requirements.Should().HaveCount(2);
        published.Requirements.Should().Contain(r =>
            r.DocumentType == "Public Liability Insurance" &&
            r.MinValidityDays == 30 &&
            r.RequiresIssuerMatch &&
            r.AcceptedIssuers.Count == 3);
    }

    [Fact]
    public void Each_publish_increments_the_version()
    {
        var profile = LogisticsProfile();
        profile.Publish(Now);

        profile.AddRequirement(DocumentType.Parse("Trade Licence"));
        profile.Publish(Now.AddDays(1));

        profile.PublishedVersion.Should().Be(2);
        profile.DomainEvents.OfType<ComplianceProfileVersionPublished>().Last()
            .ProfileVersion.Should().Be(2);
    }

    [Fact]
    public void A_profile_with_no_requirements_cannot_be_published()
    {
        // It would make every supplier in the category vacuously compliant.
        var profile = ComplianceProfile.Create("Logistics Contractor");

        var act = () => profile.Publish(Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.profile.nothing_to_publish");
    }

    [Fact]
    public void A_profile_of_only_optional_requirements_cannot_be_published()
    {
        // No supplier in the category could ever be non-compliant, which makes the category pointless.
        var profile = ComplianceProfile.Create("Logistics Contractor");
        profile.AddRequirement(DocumentType.Parse("Safety Training"), isMandatory: false);

        var act = () => profile.Publish(Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.profile.no_mandatory_requirements");
    }

    [Fact]
    public void A_requirement_is_deprecated_rather_than_deleted()
    {
        // SRS §6.1 — deleting a requirement suppliers have evidenced would orphan that evidence.
        var profile = LogisticsProfile();
        profile.Publish(Now);
        var insurance = profile.Requirements.Single(r => r.DocumentType.Value == "Public Liability Insurance");

        profile.DeprecateRequirement(insurance.Id);

        profile.Requirements.Should().HaveCount(2, "the record survives");
        profile.ActiveRequirements.Should().HaveCount(1);
        insurance.IsDeprecated.Should().BeTrue();
    }

    [Fact]
    public void A_deprecated_requirement_is_absent_from_the_next_published_version()
    {
        var profile = LogisticsProfile();
        profile.Publish(Now);
        var insurance = profile.Requirements.Single(r => r.DocumentType.Value == "Public Liability Insurance");
        profile.DeprecateRequirement(insurance.Id);
        profile.ClearDomainEvents();

        profile.Publish(Now.AddDays(1));

        profile.DomainEvents.OfType<ComplianceProfileVersionPublished>().Should().ContainSingle()
            .Which.Requirements.Should().ContainSingle()
            .Which.DocumentType.Should().Be("ISO 9001");
    }

    [Fact]
    public void Deprecating_a_document_type_frees_it_to_be_required_again()
    {
        // A requirement can legitimately be re-created with different thresholds.
        var profile = LogisticsProfile();
        var iso = profile.Requirements.Single(r => r.DocumentType.Value == "ISO 9001");
        profile.DeprecateRequirement(iso.Id);

        var replacement = profile.AddRequirement(DocumentType.Parse("ISO 9001"), renewalLeadTimeDays: 90);

        replacement.Id.Should().NotBe(iso.Id);
        profile.ActiveRequirements.Should().HaveCount(2);
    }

    [Fact]
    public void Deprecating_an_unknown_requirement_is_refused()
    {
        var profile = LogisticsProfile();

        var act = () => profile.DeprecateRequirement(RequirementId.New());

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.profile.unknown_requirement");
    }
}

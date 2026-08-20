using FluentAssertions;
using Xunit;

namespace Certiflow.SeedCorpus.Tests;

/// <summary>
/// The corpus is a specification, not test data. SRS §16.1 states the spread of compliance states
/// the demo depends on, and these tests are what stop a later tidy-up from quietly removing the
/// supplier whose certificate is deliberately wrong.
/// </summary>
public sealed class CorpusDefinitionTests
{
    private static readonly DateOnly Today = new(2026, 8, 18);

    [Fact]
    public void The_corpus_has_twelve_suppliers_across_three_categories()
    {
        CorpusDefinition.Categories.Should().HaveCount(3);
        CorpusDefinition.Suppliers(Today).Should().HaveCount(12);
    }

    [Fact]
    public void The_compliance_spread_matches_the_srs()
    {
        // SRS §16.1: 5 Compliant · 2 At Risk · 2 Non-Compliant · 2 Awaiting Review · 1 Suspended.
        // Suspended is counted separately, so the status tally covers the eleven active suppliers.
        var suppliers = CorpusDefinition.Suppliers(Today);
        var active = suppliers.Where(s => s.Status == SeedSupplierStatus.Active).ToList();

        active.Count(s => s.ExpectedStatus == ExpectedComplianceStatus.Compliant).Should().Be(5);
        active.Count(s => s.ExpectedStatus == ExpectedComplianceStatus.AtRisk).Should().Be(2);
        active.Count(s => s.ExpectedStatus == ExpectedComplianceStatus.NonCompliant).Should().Be(2);
        active.Count(s => s.ExpectedStatus == ExpectedComplianceStatus.Pending).Should().Be(2);
        suppliers.Count(s => s.Status == SeedSupplierStatus.Suspended).Should().Be(1);
    }

    [Fact]
    public void The_two_at_risk_suppliers_expire_in_nine_and_twenty_one_days()
    {
        // The nine-day supplier is what the demo drills into. If this drifts, the dashboard stops
        // telling the story the video is built around.
        var atRisk = CorpusDefinition.Suppliers(Today)
            .Where(s => s.ExpectedStatus == ExpectedComplianceStatus.AtRisk)
            .Select(s => s.Certificates.Min(c => c.ExpiresOn.DayNumber - Today.DayNumber))
            .OrderBy(days => days)
            .ToList();

        atRisk.Should().Equal([9, 21]);
    }

    [Fact]
    public void One_supplier_is_non_compliant_through_expiry_and_another_through_omission()
    {
        var suppliers = CorpusDefinition.Suppliers(Today);

        var expired = suppliers.Single(s => s.LegalName == "Cedar Haulage Group");
        expired.Certificates.Should().Contain(c => c.ExpiresOn < Today);

        // Halcyon holds two of the three mandatory Facilities requirements. Nothing was rejected;
        // the document simply never arrived, which is the more common real-world failure.
        var incomplete = suppliers.Single(s => s.LegalName == "Halcyon Maintenance Ltd");
        var mandatory = CorpusDefinition.Facilities.Requirements.Where(r => r.IsMandatory).Select(r => r.DocumentType);
        incomplete.Certificates.Select(c => c.DocumentType).Should().NotContain("Safety Training Record");
        mandatory.Should().Contain("Safety Training Record");
    }

    [Fact]
    public void Exactly_one_certificate_carries_a_deliberately_mismatched_holder_name()
    {
        var mismatched = CorpusDefinition.Suppliers(Today)
            .SelectMany(supplier => supplier.Certificates.Select(certificate => (supplier, certificate)))
            .Where(pair => pair.certificate.HolderName != pair.supplier.LegalName)
            .ToList();

        mismatched.Should().ContainSingle("SRS §16.1 calls for one, and only one, entity-match case");
        mismatched[0].certificate.DemoNote.Should().NotBeNullOrWhiteSpace(
            "the deliberate case must explain itself, or someone will 'fix' it");
    }

    [Fact]
    public void Both_languages_are_represented()
    {
        var certificates = CorpusDefinition.Suppliers(Today).SelectMany(s => s.Certificates).ToList();

        certificates.Should().Contain(c => c.Language == CorpusLanguage.French);
        certificates.Should().Contain(c => c.Language == CorpusLanguage.English);
    }

    [Fact]
    public void All_three_layouts_are_used()
    {
        // Varied layouts stop the extraction pipeline being fitted to a single template.
        CorpusDefinition.Suppliers(Today)
            .SelectMany(s => s.Certificates)
            .Select(c => c.Layout)
            .Distinct()
            .Should().HaveCount(3);
    }

    [Fact]
    public void Every_certificate_has_a_coherent_validity_period()
    {
        foreach (var certificate in CorpusDefinition.Suppliers(Today).SelectMany(s => s.Certificates))
        {
            certificate.ExpiresOn.Should().BeAfter(certificate.IssuedOn, "{0}", certificate.FileName);

            // Five years is the same plausibility bound BC5's ValidityPeriod enforces; a corpus
            // that violated it could never be approved by the system it exists to demonstrate.
            certificate.ExpiresOn.Should().BeOnOrBefore(certificate.IssuedOn.AddYears(5), "{0}", certificate.FileName);
        }
    }

    [Fact]
    public void Identifiers_are_stable_across_runs()
    {
        // Regenerating must not invalidate the seeded audit trail, the cached extractions of
        // guardrail G8, or any screenshot taken from an earlier run.
        var first = CorpusDefinition.Suppliers(Today);
        var second = CorpusDefinition.Suppliers(Today);

        first.Select(s => s.SupplierId).Should().Equal(second.Select(s => s.SupplierId));
        first.SelectMany(s => s.Certificates).Select(c => c.DocumentId)
            .Should().Equal(second.SelectMany(s => s.Certificates).Select(c => c.DocumentId));
    }

    [Fact]
    public void Identifiers_do_not_depend_on_the_reference_date()
    {
        // Only the dates move when the corpus is regenerated for a new recording session.
        var august = CorpusDefinition.Suppliers(Today);
        var october = CorpusDefinition.Suppliers(Today.AddMonths(2));

        august.Select(s => s.SupplierId).Should().Equal(october.Select(s => s.SupplierId));
        october.Single(s => s.LegalName == "Northwind Freight Ltd")
            .Certificates.Min(c => c.ExpiresOn.DayNumber - Today.AddMonths(2).DayNumber)
            .Should().Be(9, "the at-risk case must stay at-risk whenever the corpus is regenerated");
    }

    [Fact]
    public void Every_certificate_maps_to_a_requirement_in_its_supplier_category()
    {
        var suppliers = CorpusDefinition.Suppliers(Today);

        foreach (var supplier in suppliers)
        {
            var category = CorpusDefinition.Categories.Single(c => c.CategoryId == supplier.CategoryId);

            foreach (var certificate in supplier.Certificates)
            {
                category.Requirements.Should().Contain(
                    r => r.RequirementId == certificate.RequirementId,
                    "{0} references a requirement outside {1}", certificate.FileName, category.Name);
            }
        }
    }

    [Fact]
    public void Every_issuer_is_one_the_requirement_accepts()
    {
        // Otherwise the issuer-match check would fail across the whole corpus and every document
        // would need a reviewer - a corpus fault presenting as a pipeline fault.
        var suppliers = CorpusDefinition.Suppliers(Today);

        foreach (var supplier in suppliers)
        {
            var category = CorpusDefinition.Categories.Single(c => c.CategoryId == supplier.CategoryId);

            foreach (var certificate in supplier.Certificates)
            {
                var requirement = category.Requirements.Single(r => r.RequirementId == certificate.RequirementId);

                requirement.AcceptedIssuers.Should().Contain(
                    certificate.IssuerName, "{0}", certificate.FileName);
            }
        }
    }
}

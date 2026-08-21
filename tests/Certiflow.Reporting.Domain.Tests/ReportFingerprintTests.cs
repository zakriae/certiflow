using Certiflow.Reporting.Domain;
using FluentAssertions;
using Xunit;

namespace Certiflow.Reporting.Domain.Tests;

public sealed class ReportFingerprintTests
{
    private static readonly SupplierId Supplier = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly RequirementId Requirement = new(Guid.Parse("99990000-0000-0000-0000-000000000001"));

    private static readonly DocumentId Document = new(Guid.Parse("dddd0000-0000-0000-0000-000000000001"));

    private static EvidenceLine Evidence(string certificateNumber = "FR-9001-00417", string issuer = "AFNOR Certification") =>
        new(Document, certificateNumber, issuer, "Meridian Logistics SARL",
            new DateOnly(2024, 9, 26), new DateOnly(2027, 9, 26),
            "reviewer@certiflow.demo", new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));

    private static SupplierComplianceSnapshot Snapshot(
        string status = "Compliant",
        EvidenceLine? evidence = null,
        int? daysRemaining = 400) =>
        new(Supplier, "Meridian Logistics SARL", "Meridian", "FR-882-119", "FR", "Logistics", 1, status,
            new DateOnly(2026, 3, 14),
            [new ObligationLine(Requirement, "ISO 9001", true, "Satisfied", daysRemaining, evidence ?? Evidence())]);

    [Fact]
    public void The_same_facts_always_hash_the_same()
    {
        ReportFingerprint.Compute(Snapshot()).Should().Be(ReportFingerprint.Compute(Snapshot()));
    }

    [Fact]
    public void Changing_a_certificate_number_changes_the_fingerprint()
    {
        // The whole point: a hash that does not move when the facts move verifies nothing.
        ReportFingerprint.Compute(Snapshot())
            .Should().NotBe(ReportFingerprint.Compute(Snapshot(evidence: Evidence(certificateNumber: "FR-9001-00418"))));
    }

    [Fact]
    public void Changing_the_overall_status_changes_the_fingerprint()
    {
        ReportFingerprint.Compute(Snapshot())
            .Should().NotBe(ReportFingerprint.Compute(Snapshot(status: "NonCompliant")));
    }

    [Fact]
    public void Days_remaining_is_not_part_of_the_fingerprint()
    {
        // It is derived from the expiry date and the day you ask. Hashing it would make a report
        // fail its own verification tomorrow morning, for no reason anyone could explain.
        ReportFingerprint.Compute(Snapshot(daysRemaining: 400))
            .Should().Be(ReportFingerprint.Compute(Snapshot(daysRemaining: 399)));
    }

    [Fact]
    public void An_obligation_with_no_evidence_does_not_hash_like_one_with_blank_evidence()
    {
        var blank = new EvidenceLine(
            new DocumentId(Guid.Empty), string.Empty, string.Empty, string.Empty,
            default, default, string.Empty, default);

        ReportFingerprint.Compute(Snapshot(evidence: null, status: "NonCompliant"))
            .Should().NotBe(ReportFingerprint.Compute(Snapshot(evidence: blank, status: "NonCompliant")));
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted_to_forge_a_matching_hash()
    {
        // This is what the length prefixes buy. Under plain concatenation, moving a character from
        // the end of the issuer to the start of the holder name produces identical bytes - so a
        // determined editor could rewrite a report and keep its verification hash valid.
        var original = Snapshot(evidence: Evidence(issuer: "AFNOR Certification"));

        var shifted = original with
        {
            Obligations =
            [
                original.Obligations[0] with
                {
                    Evidence = original.Obligations[0].Evidence! with
                    {
                        Issuer = "AFNOR Certificatio",
                        HolderName = "nMeridian Logistics SARL",
                    },
                },
            ],
        };

        ReportFingerprint.Compute(original).Should().NotBe(ReportFingerprint.Compute(shifted));
    }

    [Fact]
    public void The_fingerprint_is_a_full_sha256_in_lowercase_hex()
    {
        ReportFingerprint.Compute(Snapshot()).Should().MatchRegex("^[0-9a-f]{64}$");
    }
}

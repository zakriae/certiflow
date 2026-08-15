using Certiflow.Intake.Domain.Events;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intake.Domain.Tests;

public sealed class DocumentTests
{
    private const string Uploader = "contact@meridian-logistics.demo";

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static readonly SupplierId Supplier = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly RequirementId Requirement = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static Sha256Hash Hash(char fill = 'a') => Sha256Hash.Parse(new string(fill, 64));

    private static StorageReference Storage() =>
        StorageReference.ForDocument("documents", Supplier, DocumentId.New(), ".pdf");

    private static Document Accept(
        string contentType = "application/pdf",
        long sizeBytes = 512 * 1024,
        int? pageCount = 3,
        RequirementId? requirementId = null,
        Sha256Hash? sha256 = null) =>
        Document.Accept(
            Supplier,
            requirementId ?? Requirement,
            expectedDocumentType: "ISO 9001",
            fileName: "iso9001-2026.pdf",
            contentType: contentType,
            sizeBytes: sizeBytes,
            sha256: sha256 ?? Hash(),
            storageReference: Storage(),
            pageCount: pageCount,
            uploadedBy: Uploader,
            uploadedAt: Now);

    [Fact]
    public void An_accepted_document_raises_the_event_that_starts_extraction()
    {
        var document = Accept();

        document.Status.Should().Be(DocumentStatus.Accepted);
        document.DomainEvents.OfType<DocumentReceived>().Should().ContainSingle();

        var stored = document.DomainEvents.OfType<DocumentStored>().Should().ContainSingle().Subject;
        stored.ExpectedDocumentType.Should().Be("ISO 9001");
        stored.StorageBlobPath.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("APPLICATION/PDF")]
    public void The_accepted_content_types_are_accepted(string contentType) =>
        Accept(contentType: contentType).ContentType.Should().Be(contentType);

    [Theory]
    [InlineData("application/zip")]
    [InlineData("text/html")]
    [InlineData("application/x-msdownload")]
    [InlineData("image/svg+xml")]
    public void Anything_outside_the_allow_list_is_refused(string contentType)
    {
        // An allow-list, not a block-list: "is this file type dangerous?" has no reliable answer,
        // "is it one of the three we accept?" does.
        var act = () => Accept(contentType: contentType);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.document.content_type_not_allowed");
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused_with_a_message_a_supplier_can_act_on()
    {
        // FR-2.2 — "reject with a clear, actionable message". Guardrail G4.
        var act = () => Accept(sizeBytes: Document.MaxSizeBytes + 1);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Should().Match<DomainRuleViolationException>(e =>
                e.Rule == "intake.document.too_large" && e.Message.Contains("20 MB", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        var act = () => Accept(sizeBytes: 0);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.document.empty_file");
    }

    [Fact]
    public void A_document_over_the_page_limit_is_refused()
    {
        // Guardrail G4 — this bound is what keeps one upload from costing 30x a normal extraction.
        var act = () => Accept(pageCount: Document.MaxPageCount + 1);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.document.too_many_pages");
    }

    [Fact]
    public void A_quarantined_document_is_still_recorded()
    {
        // FR-2.7. Returning a 400 and keeping nothing means a supplier who insists they sent it and
        // an admin with no way to check.
        var document = Document.Quarantine(
            Supplier,
            Requirement,
            expectedDocumentType: "ISO 9001",
            fileName: "scan.zip",
            contentType: "application/zip",
            sizeBytes: 4096,
            sha256: Hash('b'),
            storageReference: Storage(),
            pageCount: null,
            uploadedBy: Uploader,
            uploadedAt: Now,
            reason: "Content type application/zip is not accepted.");

        document.Status.Should().Be(DocumentStatus.Quarantined);
        document.QuarantineReason.Should().Contain("application/zip");
        document.DomainEvents.OfType<DocumentQuarantined>().Should().ContainSingle();
        document.DomainEvents.OfType<DocumentStored>().Should().BeEmpty("nothing should try to extract it");
    }

    [Fact]
    public void Superseding_records_the_replacement_without_touching_the_file()
    {
        var original = Accept();
        var replacementId = DocumentId.New();

        original.SupersededBy(replacementId, Requirement);

        original.Status.Should().Be(DocumentStatus.Superseded);
        original.SupersededByDocumentId.Should().Be(replacementId);
        original.StorageReference.Should().NotBeNull("the bytes are never altered");
        original.DomainEvents.OfType<DocumentSuperseded>().Should().ContainSingle()
            .Which.SupersedingDocumentId.Should().Be(replacementId);
    }

    [Fact]
    public void A_document_cannot_be_superseded_by_one_for_a_different_requirement()
    {
        // Otherwise evidence for an insurance certificate could be replaced by an ISO certificate,
        // and BC5 would faithfully record it.
        var document = Accept();
        var otherRequirement = new RequirementId(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        var act = () => document.SupersededBy(DocumentId.New(), otherRequirement);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.document.supersede_requirement_mismatch");
    }

    [Fact]
    public void A_document_cannot_supersede_itself()
    {
        var document = Accept();

        var act = () => document.SupersededBy(document.Id, Requirement);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.document.cannot_supersede_itself");
    }

    [Fact]
    public void An_already_superseded_document_cannot_be_superseded_again()
    {
        var document = Accept();
        document.SupersededBy(DocumentId.New(), Requirement);

        var act = () => document.SupersededBy(DocumentId.New(), Requirement);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.document.only_accepted_can_be_superseded");
    }

    [Fact]
    public void The_same_bytes_for_the_same_requirement_are_a_duplicate()
    {
        var existing = Accept(sha256: Hash('c'));

        existing.IsDuplicateOf(Hash('c'), Supplier, Requirement).Should().BeTrue();
    }

    [Fact]
    public void The_same_bytes_for_a_different_requirement_are_not_a_duplicate()
    {
        // One certificate can legitimately evidence two requirements.
        var existing = Accept(sha256: Hash('c'));
        var otherRequirement = new RequirementId(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        existing.IsDuplicateOf(Hash('c'), Supplier, otherRequirement).Should().BeFalse();
    }

    [Fact]
    public void A_quarantined_document_does_not_block_a_corrected_resubmission()
    {
        // Otherwise a supplier who uploaded a bad file could never upload it again after fixing the
        // problem that caused the quarantine, because the bytes might be unchanged.
        var quarantined = Document.Quarantine(
            Supplier, Requirement, "ISO 9001", "scan.pdf", "application/pdf",
            4096, Hash('d'), Storage(), null, Uploader, Now, "Password protected.");

        quarantined.IsDuplicateOf(Hash('d'), Supplier, Requirement).Should().BeFalse();
    }
}

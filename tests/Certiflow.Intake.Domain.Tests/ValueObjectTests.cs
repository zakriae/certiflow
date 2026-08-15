using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intake.Domain.Tests;

public sealed class Sha256HashTests
{
    private const string Valid = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Parses_a_valid_hash()
    {
        Sha256Hash.Parse(Valid).Value.Should().Be(Valid);
    }

    [Fact]
    public void Normalises_case_so_a_duplicate_check_cannot_be_defeated_by_it()
    {
        // If casing survived, the same certificate could be submitted twice (FR-2.4).
        Sha256Hash.Parse(Valid.ToUpperInvariant()).Should().Be(Sha256Hash.Parse(Valid));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    public void Refuses_a_hash_of_the_wrong_length(string value)
    {
        var act = () => Sha256Hash.Parse(value);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.sha256.wrong_length");
    }

    [Fact]
    public void Refuses_a_hash_that_is_not_hexadecimal()
    {
        var act = () => Sha256Hash.Parse(new string('z', 64));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.sha256.not_hex");
    }
}

public sealed class StorageReferenceTests
{
    private static readonly SupplierId Supplier = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Lays_documents_out_under_a_per_supplier_prefix()
    {
        // A mis-scoped SAS then leaks one supplier's documents rather than every supplier's (NFR-8).
        var documentId = DocumentId.New();

        var reference = StorageReference.ForDocument("documents", Supplier, documentId, ".pdf");

        reference.Container.Should().Be("documents");
        reference.BlobPath.Should().Be($"{Supplier.Value:D}/{documentId.Value:D}.pdf");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("supplier/../../other-supplier/doc.pdf")]
    [InlineData("/absolute/path.pdf")]
    public void Refuses_a_path_that_could_climb_out_of_its_prefix(string blobPath)
    {
        var act = () => StorageReference.Create("documents", blobPath);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intake.storage.path_not_relative");
    }

    [Fact]
    public void Is_a_reference_not_a_url()
    {
        // A URL implies reachability. These containers are private and access is a short-lived SAS
        // minted at download time (FR-2.5, NFR-10).
        var reference = StorageReference.Create("documents", "supplier-1/doc.pdf");

        reference.ToString().Should().NotStartWith("http");
    }
}

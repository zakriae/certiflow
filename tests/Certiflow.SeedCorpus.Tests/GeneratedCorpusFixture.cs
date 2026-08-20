using Certiflow.Intelligence.Domain.Grounding;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Certiflow.SeedCorpus.Tests;

/// <summary>
/// Generates the corpus once for the whole test class and parses every PDF back with PdfPig -
/// the same library BC3's infrastructure will use, so what these tests read is what the
/// extraction pipeline will read.
/// </summary>
public sealed class GeneratedCorpusFixture : IDisposable
{
    /// <summary>Fixed so the corpus is identical on every run and on every machine.</summary>
    public static readonly DateOnly ReferenceDate = new(2026, 8, 18);

    public GeneratedCorpusFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"certiflow-corpus-{Guid.CreateVersion7():N}");
        Manifest = CorpusGenerator.Generate(Directory, ReferenceDate);

        Parsed = Manifest.Suppliers
            .SelectMany(supplier => supplier.Certificates)
            .ToDictionary(certificate => certificate.DocumentId, certificate => Parse(certificate.FileName));
    }

    public string Directory { get; }

    public SeedCorpusManifest Manifest { get; }

    /// <summary>Each certificate's PDF, parsed into the domain's own <see cref="ParsedDocument"/>.</summary>
    public IReadOnlyDictionary<Guid, ParsedDocument> Parsed { get; }

    public IEnumerable<SeedCertificate> Certificates =>
        Manifest.Suppliers.SelectMany(supplier => supplier.Certificates);

    private ParsedDocument Parse(string fileName)
    {
        var path = Path.Combine(Directory, "certificates", fileName);

        using var document = PdfDocument.Open(path);

        var pages = document.GetPages()
            .Select(page => new DocumentPage(page.Number, ContentOrderTextExtractor.GetText(page) ?? string.Empty))
            .ToList();

        return new ParsedDocument(pages, TextSource.EmbeddedTextLayer);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}

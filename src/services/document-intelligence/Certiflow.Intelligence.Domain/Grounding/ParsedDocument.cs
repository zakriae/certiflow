using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Grounding;

/// <summary>One page of extracted text, keyed by its 1-based page number.</summary>
public sealed record DocumentPage
{
    public DocumentPage(int pageNumber, string text)
    {
        Guard.Require(
            pageNumber >= 1,
            "intelligence.page.number_out_of_range",
            $"Page number must be 1 or greater, but was {pageNumber}.");

        PageNumber = pageNumber;
        Text = text ?? string.Empty;
        NormalizedText = TextNormalizer.Normalize(Text);
    }

    public int PageNumber { get; }

    /// <summary>Text as the parser produced it. Shown to reviewers; never used for matching.</summary>
    public string Text { get; }

    /// <summary>Normalised once at construction — grounding compares against this.</summary>
    public string NormalizedText { get; }

    public bool HasTextLayer => NormalizedText.Length > 0;
}

/// <summary>
/// The source of truth a citation is checked against: the document's text, page by page.
/// <para>
/// Produced by Infrastructure (PdfPig for text-layer PDFs, Azure AI Document Intelligence for
/// scans — FR-3.6) and handed to the domain as plain data. The domain never opens a file, which
/// is what lets every grounding test in this repo run in microseconds against a string literal.
/// </para>
/// </summary>
public sealed class ParsedDocument
{
    private readonly Dictionary<int, DocumentPage> _pages;

    public ParsedDocument(IReadOnlyCollection<DocumentPage> pages, TextSource source)
    {
        Guard.AgainstNull(pages, "intelligence.parsed_document.pages_required");

        Guard.Require(
            pages.Count > 0,
            "intelligence.parsed_document.no_pages",
            "A parsed document must have at least one page.");

        var duplicates = pages.GroupBy(p => p.PageNumber).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Guard.Require(
            duplicates.Count == 0,
            "intelligence.parsed_document.duplicate_pages",
            $"Duplicate page numbers in parsed document: {string.Join(", ", duplicates)}.");

        _pages = pages.ToDictionary(p => p.PageNumber);
        Source = source;
    }

    public TextSource Source { get; }

    public int PageCount => _pages.Count;

    public IReadOnlyCollection<DocumentPage> Pages => _pages.Values;

    /// <summary>
    /// True when no page yielded any text — a scan with no text layer. The pipeline branches on
    /// this to decide whether OCR is needed before extraction (SRS §8.2).
    /// </summary>
    public bool IsEmpty => _pages.Values.All(p => !p.HasTextLayer);

    public DocumentPage? Page(int pageNumber) => _pages.GetValueOrDefault(pageNumber);
}

/// <summary>
/// How the text was obtained. Recorded because it changes how much a grounding failure means:
/// an unlocatable snippet in a clean text layer is a hallucination, whereas in OCR output it may
/// simply be a misread character. Both still score zero — but a reviewer deserves to know which.
/// </summary>
public enum TextSource
{
    /// <summary>Extracted from the PDF's own text layer.</summary>
    EmbeddedTextLayer = 1,

    /// <summary>Produced by OCR over a scanned image (FR-3.6).</summary>
    OpticalCharacterRecognition = 2,
}

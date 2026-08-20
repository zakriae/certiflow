using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Domain.Grounding;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Certiflow.Intelligence.Infrastructure.Parsing;

/// <summary>
/// Extracts text from a PDF, page by page, with PdfPig.
/// <para>
/// <b>This is what makes citations verifiable rather than decorative.</b> The model claims a
/// snippet came from page 3; without a parser that reports page numbers there is no way to check
/// that claim, and "grounded extraction" becomes a slogan.
/// </para>
/// <para>
/// Uses <c>ContentOrderTextExtractor</c> rather than the raw <c>page.Text</c>. Raw text
/// concatenates glyphs in content-stream order, which for a two-column or table layout interleaves
/// unrelated lines and inserts no spaces between words — a snippet that is plainly on the page then
/// fails to match, grounding vetoes confidence to zero, and a correct document goes to a reviewer.
/// The corpus tests assert this end of it holds for every generated certificate.
/// </para>
/// </summary>
public sealed class PdfPigDocumentTextParser : IDocumentTextParser
{
    public async Task<ParsedDocument> ParseAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // PdfPig is synchronous and needs random access. Buffering first keeps a network or blob
        // stream from being read twice, and keeps the sync work off the caller's await path.
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        cancellationToken.ThrowIfCancellationRequested();

        using var pdf = PdfDocument.Open(buffer);

        var pages = pdf.GetPages()
            .Select(page => new DocumentPage(page.Number, ContentOrderTextExtractor.GetText(page) ?? string.Empty))
            .ToList();

        return new ParsedDocument(pages, TextSource.EmbeddedTextLayer);
    }
}

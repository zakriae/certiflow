using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Grounding;

/// <summary>Whether a citation could be traced back to the source document (SRS §8.1).</summary>
public enum GroundingResult
{
    /// <summary>No citation was supplied, so nothing could be checked.</summary>
    NotAttempted = 0,

    /// <summary>The snippet was located in the document's text.</summary>
    Verified = 1,

    /// <summary>
    /// The snippet is not in the document. The model produced a value it did not read —
    /// confidence is forced to zero and the field goes to a human (FR-3.4).
    /// </summary>
    NotFoundInSource = 2,
}

/// <summary>The outcome of one grounding check.</summary>
/// <param name="Result">Whether the snippet was found.</param>
/// <param name="Citation">
/// The citation with discovered offsets, and with its page corrected if the snippet turned out
/// to be on a different page than claimed. Null when there was nothing to check.
/// </param>
/// <param name="PageMismatch">
/// True when the text was found, but not on the page the model claimed. The value is still
/// grounded — it really is in the document — so it keeps full grounding credit; the flag exists
/// so the reviewer sees the discrepancy and the preview jumps to the page that actually contains
/// the text (FR-4.3).
/// </param>
/// <param name="Detail">Human-readable reason, surfaced to reviewers and written to the audit trail.</param>
public sealed record GroundingCheck(
    GroundingResult Result,
    Citation? Citation,
    bool PageMismatch,
    string? Detail)
{
    public bool IsGrounded => Result == GroundingResult.Verified;
}

/// <summary>
/// <b>The check that answers "what stops the AI hallucinating a date?" (SRS §19 Q3).</b>
/// <para>
/// The model is asked for a value <em>and</em> the verbatim text it read that value from. This
/// class then looks for that text in the document itself. A model that invents an expiry date
/// must also invent the sentence containing it, and an invented sentence is not in the PDF — so
/// the check catches the fabrication without ever asking the model whether it was sure.
/// </para>
/// <para>
/// Matching is exact after normalisation (<see cref="TextNormalizer"/>), never fuzzy. Fuzzy
/// matching would be indefensible here: the whole guarantee is "this text is in the document",
/// and "something 85% like this text is in the document" is not the same guarantee.
/// </para>
/// </summary>
public static class GroundingVerifier
{
    public static GroundingCheck Verify(Citation? citation, ParsedDocument document)
    {
        Guard.AgainstNull(document, "intelligence.grounding.document_required");

        if (citation is null)
        {
            return new GroundingCheck(
                GroundingResult.NotAttempted,
                Citation: null,
                PageMismatch: false,
                Detail: "No citation was supplied for this field.");
        }

        var needle = TextNormalizer.Normalize(citation.Snippet);

        if (needle.Length == 0)
        {
            return new GroundingCheck(
                GroundingResult.NotFoundInSource,
                citation,
                PageMismatch: false,
                Detail: "The citation snippet contained no comparable text.");
        }

        // The cited page first — the common case, and the one that needs no caveat.
        var citedPage = document.Page(citation.PageNumber);

        if (citedPage is not null)
        {
            var offset = citedPage.NormalizedText.IndexOf(needle, StringComparison.Ordinal);

            if (offset >= 0)
            {
                return new GroundingCheck(
                    GroundingResult.Verified,
                    citation.Located(offset, offset + needle.Length),
                    PageMismatch: false,
                    Detail: null);
            }
        }

        // Not on the cited page. Before calling it a hallucination, check the rest of the
        // document: models routinely get the value right and the page number wrong, and treating
        // that as fabrication would send correct extractions to a reviewer for no reason.
        foreach (var page in document.Pages.OrderBy(p => p.PageNumber))
        {
            if (page.PageNumber == citation.PageNumber)
            {
                continue;
            }

            var offset = page.NormalizedText.IndexOf(needle, StringComparison.Ordinal);

            if (offset < 0)
            {
                continue;
            }

            var relocated = new Citation(page.PageNumber, citation.Snippet, offset, offset + needle.Length);

            return new GroundingCheck(
                GroundingResult.Verified,
                relocated,
                PageMismatch: true,
                Detail: $"Snippet was cited on page {citation.PageNumber} but found on page {page.PageNumber}.");
        }

        var reason = citedPage is null
            ? $"Cited page {citation.PageNumber} does not exist; the document has {document.PageCount} page(s)."
            : $"The cited text was not found anywhere in the document ({document.Source}).";

        return new GroundingCheck(
            GroundingResult.NotFoundInSource,
            citation,
            PageMismatch: false,
            Detail: reason);
    }
}

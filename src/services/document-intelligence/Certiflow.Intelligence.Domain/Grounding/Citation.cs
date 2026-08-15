using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Grounding;

/// <summary>
/// Proof of provenance: a page number plus the verbatim snippet the model says it read the value
/// from (SRS §3).
/// <para>
/// A citation is a <em>claim</em>, not evidence. It becomes evidence only once
/// <see cref="GroundingVerifier"/> finds the snippet in the parsed source text. The character
/// offsets are filled in by that verification, not by the model — which is the point: a model
/// cannot be trusted to report where in a document it found something, so the offsets are
/// discovered independently and are what drives the UI's highlight (FR-4.3).
/// </para>
/// </summary>
public sealed record Citation
{
    /// <summary>
    /// A snippet must be long enough to be distinctive. "2026" appears on every page of a
    /// certificate; grounding it proves nothing, so short snippets are refused outright rather
    /// than being allowed to score a spurious 0.40.
    /// </summary>
    public const int MinimumSnippetLength = 8;

    public const int MaximumSnippetLength = 500;

    public Citation(int pageNumber, string snippet, int? charOffsetStart = null, int? charOffsetEnd = null)
    {
        Guard.Require(
            pageNumber >= 1,
            "intelligence.citation.page_out_of_range",
            $"Citation page must be 1 or greater, but was {pageNumber}.");

        var trimmed = Guard.AgainstNullOrWhiteSpace(snippet, "intelligence.citation.snippet_required");

        Guard.Require(
            trimmed.Length >= MinimumSnippetLength,
            "intelligence.citation.snippet_too_short",
            $"Citation snippet must be at least {MinimumSnippetLength} characters to be distinctive, but was {trimmed.Length}.");

        Guard.AgainstTooLong(trimmed, MaximumSnippetLength, "intelligence.citation.snippet_too_long");

        PageNumber = pageNumber;
        Snippet = trimmed;
        CharOffsetStart = charOffsetStart;
        CharOffsetEnd = charOffsetEnd;
    }

    public int PageNumber { get; }

    /// <summary>The text as the model reported it, kept unmodified so a reviewer sees the claim.</summary>
    public string Snippet { get; }

    /// <summary>Offset into the normalised page text, discovered by grounding. Null until verified.</summary>
    public int? CharOffsetStart { get; }

    public int? CharOffsetEnd { get; }

    public bool IsLocated => CharOffsetStart is not null && CharOffsetEnd is not null;

    /// <summary>Returns a copy carrying the offsets grounding actually found.</summary>
    public Citation Located(int charOffsetStart, int charOffsetEnd) =>
        new(PageNumber, Snippet, charOffsetStart, charOffsetEnd);
}

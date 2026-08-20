using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;

namespace Certiflow.Intelligence.Application.Abstractions;

/// <summary>
/// Turns document bytes into text with a page map.
/// <para>
/// The page map is the point. Grounding needs to know which page a snippet came from, so a parser
/// that returns one flat string would make citations unverifiable and the review screen unable to
/// jump anywhere (FR-4.3).
/// </para>
/// </summary>
public interface IDocumentTextParser
{
    Task<ParsedDocument> ParseAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// What one call to the model produced.
/// <para>
/// Prompt and completion tokens are reported <b>separately and always</b>, including on failure.
/// With a reasoning model the completion side is the larger and less predictable half — reasoning
/// tokens bill as completion and never appear in the response — so a cost figure derived from
/// input alone is wrong (SRS §22.2, guardrail G4).
/// </para>
/// </summary>
public sealed record FieldExtractionOutcome(
    IReadOnlyList<FieldCandidate> Candidates,
    string ModelUsed,
    string PromptVersion,
    int PromptTokens,
    int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// <b>The anti-corruption layer around the model provider (SRS §4.3, §19 Q11).</b>
/// <para>
/// The domain speaks this interface and nothing else. No Azure SDK type, no OpenAI response
/// object and no HTTP concept crosses into it. The model provider is the most volatile dependency
/// in the system — the model this project was designed around was deprecated between the design
/// document and the build — and the whole point of this seam is that such a change is one class,
/// not a refactor.
/// </para>
/// </summary>
public interface IFieldExtractor
{
    Task<FieldExtractionOutcome> ExtractAsync(
        ParsedDocument document,
        DocumentTypeSchema schema,
        CancellationToken cancellationToken);
}

/// <summary>
/// Supplies the extraction contract for a document type.
/// <para>
/// Schemas are configuration, not code: adding a document type means adding a schema (FR-3.9).
/// </para>
/// </summary>
public interface IDocumentTypeSchemaProvider
{
    DocumentTypeSchema? Find(string documentType);

    IReadOnlyList<string> KnownDocumentTypes { get; }
}

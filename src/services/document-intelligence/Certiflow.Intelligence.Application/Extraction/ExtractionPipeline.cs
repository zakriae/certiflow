using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Domain;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Certiflow.Intelligence.Application.Extraction;

/// <summary>One document to extract, and everything the checks need to score it.</summary>
public sealed record ExtractionRequest(
    DocumentId DocumentId,
    SupplierId SupplierId,
    RequirementId RequirementId,
    DocumentTypeSchema Schema,
    ExtractionContext Context,
    Confidence AutoAcceptThreshold);

/// <summary>The finished job plus the scored fields, ready to publish.</summary>
public sealed record ExtractionOutcome(
    ExtractionJob Job,
    IReadOnlyList<ExtractedField> Fields,
    ParsedDocument Document);

/// <summary>
/// Runs the pipeline of SRS §8.2: parse → extract → ground → score.
/// <para>
/// Deliberately thin. Every interesting decision — whether a citation is real, what a field is
/// worth, whether the job may complete — belongs to the domain and is already tested there. This
/// class exists to sequence those steps around two pieces of I/O and to make sure a failure spends
/// an attempt rather than vanishing.
/// </para>
/// </summary>
public sealed class ExtractionPipeline(
    IDocumentTextParser parser,
    IFieldExtractor extractor,
    ILogger<ExtractionPipeline> logger)
{
    public async Task<ExtractionOutcome> RunAsync(
        ExtractionRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        Guard.AgainstNull(request, "intelligence.pipeline.request_required");
        Guard.AgainstNull(content, "intelligence.pipeline.content_required");

        var job = ExtractionJob.Create(
            request.DocumentId,
            request.SupplierId,
            request.RequirementId,
            request.Schema.DocumentType,
            request.AutoAcceptThreshold);

        // The attempt is opened before any work happens, so a crash mid-extraction still costs one
        // of the three. Otherwise a crash loop retries forever and the attempt budget of FR-3.7
        // protects nothing.
        job.BeginAttempt(modelUsed: "pending", promptVersion: request.Schema.SchemaVersion);

        var document = await parser.ParseAsync(content, cancellationToken);

        if (document.IsEmpty)
        {
            // No text layer. OCR is the documented fallback (FR-3.6); until it exists this fails
            // loudly rather than sending the model an empty page and scoring whatever comes back.
            job.FailAttempt("The document has no extractable text layer.");

            ExtractionLog.NoTextLayer(logger, request.DocumentId.Value, document.PageCount);

            return new ExtractionOutcome(job, [], document);
        }

        job.MarkExtracting(document.Source);

        var extraction = await extractor.ExtractAsync(document, request.Schema, cancellationToken);

        job.MarkGrounding();

        // The whole differentiator, in one call: ground every citation and compute confidence from
        // deterministic checks. Nothing here asks the model how sure it was.
        var fields = FieldEvaluator.Evaluate(request.Schema, extraction.Candidates, document, request.Context);

        job.Complete(request.Schema, fields, extraction.TotalTokens);

        ExtractionLog.Completed(
            logger,
            request.DocumentId.Value,
            job.OverallConfidence.Value,
            job.IsAutoAcceptable,
            extraction.PromptTokens,
            extraction.CompletionTokens);

        return new ExtractionOutcome(job, fields, document);
    }
}

internal static partial class ExtractionLog
{
    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Warning,
        Message = "Document {DocumentId} has no text layer across {PageCount} page(s); OCR fallback is required")]
    public static partial void NoTextLayer(ILogger logger, Guid documentId, int pageCount);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Information,
        Message = "Extracted {DocumentId} at confidence {Confidence} (auto-acceptable: {AutoAcceptable}); "
                + "tokens prompt={PromptTokens} completion={CompletionTokens}")]
    public static partial void Completed(
        ILogger logger,
        Guid documentId,
        decimal confidence,
        bool autoAcceptable,
        int promptTokens,
        int completionTokens);
}

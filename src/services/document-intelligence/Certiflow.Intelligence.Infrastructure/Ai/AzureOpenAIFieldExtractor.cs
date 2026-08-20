using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Certiflow.Intelligence.Infrastructure.Ai;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>e.g. <c>https://certiflow-openai-zk.openai.azure.com/</c>. Not a secret.</summary>
    public Uri? Endpoint { get; set; }

    public string DeploymentName { get; set; } = "gpt-5-mini";

    /// <summary>
    /// Guardrail G4's output ceiling. Load-bearing with a reasoning model: reasoning tokens bill as
    /// completion and are invisible in the response, so capping input alone leaves the larger and
    /// less predictable half of the bill unbounded (SRS §22.2).
    /// </summary>
    public int MaxCompletionTokens { get; set; } = 4000;
}

/// <summary>
/// <b>The anti-corruption layer around Azure OpenAI (SRS §4.3).</b>
/// <para>
/// The one class in the system that knows a model provider exists. Everything it returns is a
/// domain type; nothing it accepts is a vendor type. When <c>gpt-4o-mini</c> was deprecated
/// between the design document and the build, this is the only file that had to know.
/// </para>
/// <para>
/// Authenticates with <see cref="Azure.Identity.DefaultAzureCredential"/> — the developer's
/// <c>az login</c> session locally, managed identity in Azure. No API key is created or stored,
/// which is what NFR-9 asks for rather than a key hidden in user-secrets.
/// </para>
/// </summary>
public sealed class AzureOpenAIFieldExtractor : IFieldExtractor
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    private readonly ChatClient _chat;
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<AzureOpenAIFieldExtractor> _logger;

    public AzureOpenAIFieldExtractor(
        AzureOpenAIClient client,
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIFieldExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(client);

        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _chat = client.GetChatClient(_options.DeploymentName);
    }

    public async Task<FieldExtractionOutcome> ExtractAsync(
        ParsedDocument document,
        DocumentTypeSchema schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(schema);

        // ── Why this is built as raw JSON rather than ChatCompletionOptions ──────────────────
        // The SDK's typed MaxOutputTokenCount serialises to `max_tokens`, which every
        // gpt-5-generation model rejects outright with HTTP 400; it wants `max_completion_tokens`.
        // Pinning a newer service API version does not change it. Dropping the cap instead was not
        // an option: with a reasoning model the completion side is the larger and less predictable
        // half of the bill, so an uncapped output is an uncapped cost, and guardrail G4 is marked
        // non-negotiable in the SRS. Building the request here keeps the guardrail and keeps the
        // vendor's quirks inside the anti-corruption layer, which is where they belong.
        var request = new
        {
            messages = new object[]
            {
                new { role = "system", content = ExtractionPrompt.SystemMessage },
                new { role = "user", content = ExtractionPrompt.UserMessage(document, schema) },
            },
            // Temperature is deliberately absent. Reasoning models reject a non-default value, and
            // a strict schema is already the determinism control that matters here.
            max_completion_tokens = _options.MaxCompletionTokens,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "certificate_fields",
                    strict = true,
                    schema = JsonSerializer.Deserialize<JsonElement>(ExtractionPrompt.JsonSchema(schema)),
                },
            },
        };

        var content = BinaryContent.Create(BinaryData.FromString(JsonSerializer.Serialize(request)));

        ClientResult result;

        try
        {
            result = await _chat.CompleteChatAsync(
                content, new RequestOptions { CancellationToken = cancellationToken });
        }
        catch (ClientResultException exception)
        {
            // The provider refused the request. Wrapped so the worker retries it under FR-3.7
            // rather than treating it as a bad document and abandoning the job.
            throw new ExtractionProviderException(
                $"Azure OpenAI rejected the request ({exception.Status}): {exception.Message}", exception);
        }

        using var payload = JsonDocument.Parse(result.GetRawResponse().Content.ToMemory());
        var root = payload.RootElement;

        var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
        var promptTokens = ReadInt(usage, "prompt_tokens");
        var completionTokens = ReadInt(usage, "completion_tokens");

        var choice = root.GetProperty("choices")[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var reason) ? reason.GetString() : null;

        if (string.Equals(finishReason, "length", StringComparison.Ordinal))
        {
            // The cap cut the answer off mid-JSON. Failing is right: parsing a truncated result
            // would produce fields that look extracted but are arbitrary.
            throw new ExtractionProviderException(
                $"The model hit the {_options.MaxCompletionTokens}-token output cap before completing. "
                + "Raise AzureOpenAI:MaxCompletionTokens or reduce the document size.");
        }

        var message = choice.GetProperty("message");
        var text = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExtractionProviderException(
                $"The model returned no content (finish reason: {finishReason ?? "unknown"}).");
        }

        var candidates = Parse(text, schema);

        ExtractorLog.Extracted(
            _logger, _options.DeploymentName, candidates.Count, promptTokens, completionTokens);

        return new FieldExtractionOutcome(
            candidates,
            ModelUsed: _options.DeploymentName,
            PromptVersion: ExtractionPrompt.Version,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens);
    }

    /// <summary>
    /// Maps the model's JSON into domain <see cref="FieldCandidate"/> values.
    /// <para>
    /// Nothing is trusted here beyond its shape. A citation that is too short, unparseable or
    /// missing a page becomes a candidate with no citation at all — which grounding then treats as
    /// unverifiable and the scorer vetoes to zero. Silently repairing a malformed citation would
    /// defeat the check it exists to feed.
    /// </para>
    /// </summary>
    private static List<FieldCandidate> Parse(string json, DocumentTypeSchema schema)
    {
        ExtractionResponse? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ExtractionResponse>(json, ResponseJson);
        }
        catch (JsonException exception)
        {
            throw new ExtractionProviderException(
                "The model returned content that was not valid JSON despite a strict schema.", exception);
        }

        if (parsed?.Fields is null)
        {
            throw new ExtractionProviderException("The model's response contained no fields array.");
        }

        var candidates = new List<FieldCandidate>();

        foreach (var item in parsed.Fields)
        {
            if (string.IsNullOrWhiteSpace(item.Field) || schema.Field(item.Field) is null)
            {
                // A field the schema does not declare. Ignored rather than passed along: a model
                // returning extra keys must not be able to widen the contract.
                continue;
            }

            candidates.Add(new FieldCandidate(item.Field, item.Value, BuildCitation(item)));
        }

        return candidates;
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static Citation? BuildCitation(ExtractionResponseField item)
    {
        if (item.CitationPage is not { } page || string.IsNullOrWhiteSpace(item.CitationSnippet))
        {
            return null;
        }

        try
        {
            return new Citation(page, item.CitationSnippet);
        }
        catch (SharedKernel.DomainRuleViolationException)
        {
            // Too short to be distinctive, or an impossible page. Treated as no citation, so the
            // field is scored as ungrounded rather than credited for an unusable one.
            return null;
        }
    }

    private sealed record ExtractionResponse(
        [property: JsonPropertyName("fields")] IReadOnlyList<ExtractionResponseField>? Fields);

    private sealed record ExtractionResponseField(
        [property: JsonPropertyName("field")] string? Field,
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("citationPage")] int? CitationPage,
        [property: JsonPropertyName("citationSnippet")] string? CitationSnippet);
}

/// <summary>
/// A failure that came from the model provider rather than the document. Distinct so the worker can
/// retry it under FR-3.7 instead of treating it as a bad document and abandoning the job.
/// </summary>
public sealed class ExtractionProviderException : Exception
{
    public ExtractionProviderException(string message) : base(message)
    {
    }

    public ExtractionProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static partial class ExtractorLog
{
    [LoggerMessage(
        EventId = 3310,
        Level = LogLevel.Information,
        Message = "{Deployment} returned {FieldCount} field(s); tokens prompt={PromptTokens} completion={CompletionTokens}")]
    public static partial void Extracted(
        ILogger logger, string deployment, int fieldCount, int promptTokens, int completionTokens);
}

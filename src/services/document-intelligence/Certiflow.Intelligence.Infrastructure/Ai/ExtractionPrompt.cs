using System.Globalization;
using System.Text;
using System.Text.Json;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;

namespace Certiflow.Intelligence.Infrastructure.Ai;

/// <summary>
/// Builds the prompt and the JSON schema handed to the model as a structured-output constraint.
/// <para>
/// Both are generated from the same <see cref="DocumentTypeSchema"/> that drives the validators,
/// so the three can never disagree about what a document type contains. Adding a document type is
/// adding a schema file (FR-3.9).
/// </para>
/// </summary>
public static class ExtractionPrompt
{
    /// <summary>
    /// Bumped whenever the wording below changes. Recorded on every job (FR-3.8), so a shift in
    /// extraction quality can be traced to the prompt change that caused it rather than guessed at.
    /// </summary>
    public const string Version = "extract-v2";

    /// <summary>
    /// Rough input ceiling in characters, standing in for guardrail G4's 15k-token limit at the
    /// usual ~4 characters per token. Deliberately conservative and deliberately approximate:
    /// counting tokens exactly would mean shipping a tokeniser that has to track the model's, and
    /// the cost of being slightly under is nothing while the cost of being over is a rejected
    /// request mid-demo.
    /// </summary>
    public const int MaxDocumentCharacters = 55_000;

    public static string SystemMessage =>
        """
        You extract compliance fields from certificate documents.

        Rules, in order of importance:

        1. For every field you return, also return the exact text you read it from, copied
           character for character from the document. This is a citation. It is checked against the
           document afterwards, so a snippet that is paraphrased, reformatted or invented will be
           detected and the field discarded.
        2. Copy a snippet of at least 8 characters that appears on one line, and give the page
           number it appears on.
        3. If a field is not present in the document, return null for its value and null for its
           citation. Do not guess, infer or reconstruct a value from context. A missing field is a
           correct answer.
        4. Return the value only, never the label printed beside it. A document reading
           "Certificate No. GB-QMS-769795" gives the value "GB-QMS-769795", and one reading
           "Domaine d'application : Transport de marchandises" gives "Transport de marchandises".
           The label belongs in the citation, not in the value.
        5. Return dates as yyyy-MM-dd. The citation must still be the verbatim text as printed,
           so a certificate reading "Valable jusqu'au 26 septembre 2027" gives the value
           "2027-09-26" and a citation containing "26 septembre 2027".
        6. Return every field named in the schema, including the ones you could not find.

        Do not explain your reasoning. Return only the structured result.
        """;

    /// <summary>Renders the document as page-tagged text so the model can cite a page at all.</summary>
    public static string UserMessage(ParsedDocument document, DocumentTypeSchema schema)
    {
        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"Document type: {schema.DocumentType}");
        builder.AppendLine();
        builder.AppendLine("Fields to extract:");

        foreach (var field in schema.Fields)
        {
            var kind = field.ValueType switch
            {
                FieldValueType.Date => "date, as printed",
                FieldValueType.Enumeration => $"one of: {string.Join(", ", field.AllowedValues)}",
                _ => "text",
            };

            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"  - {field.Name} ({kind}){(field.IsMandatory ? ", required" : ", optional")}");
        }

        builder.AppendLine();
        builder.AppendLine("Document:");

        var remaining = MaxDocumentCharacters;

        foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
        {
            if (remaining <= 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"[Page {page.PageNumber} omitted: input limit reached]");
                continue;
            }

            var text = page.Text.Length > remaining ? page.Text[..remaining] : page.Text;
            remaining -= text.Length;

            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"[Page {page.PageNumber}]");
            builder.AppendLine(text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The JSON schema for structured outputs, generated from the document type's fields.
    /// <para>
    /// <c>strict</c> mode requires every property to be listed in <c>required</c> and
    /// <c>additionalProperties</c> to be false, so "not found" is expressed as an explicit null
    /// rather than an absent key. That is the right shape anyway: an absent key is ambiguous
    /// between "missing from the document" and "the model forgot".
    /// </para>
    /// </summary>
    public static BinaryData JsonSchema(DocumentTypeSchema schema)
    {
        var fieldNames = schema.Fields.Select(field => field.Name).ToArray();

        var payload = new
        {
            type = "object",
            properties = new
            {
                fields = new
                {
                    type = "array",
                    description = "One entry per field named in the request, including fields that were not found.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            field = new { type = "string", @enum = fieldNames },
                            value = new
                            {
                                type = new[] { "string", "null" },
                                description = "The value exactly as printed, or null if absent.",
                            },
                            citationPage = new
                            {
                                type = new[] { "integer", "null" },
                                description = "1-based page the snippet appears on, or null.",
                            },
                            citationSnippet = new
                            {
                                type = new[] { "string", "null" },
                                description = "Verbatim text from the document containing the value, or null.",
                            },
                        },
                        required = new[] { "field", "value", "citationPage", "citationSnippet" },
                        additionalProperties = false,
                    },
                },
            },
            required = new[] { "fields" },
            additionalProperties = false,
        };

        return BinaryData.FromString(JsonSerializer.Serialize(payload));
    }
}

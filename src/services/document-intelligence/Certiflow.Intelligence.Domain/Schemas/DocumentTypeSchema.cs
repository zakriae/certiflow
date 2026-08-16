using System.Text.RegularExpressions;
using Certiflow.SharedKernel;

namespace Certiflow.Intelligence.Domain.Schemas;

/// <summary>What kind of value a field holds, and therefore how it is validated.</summary>
public enum FieldValueType
{
    Text = 1,
    Date = 2,

    /// <summary>One of a closed set, e.g. <c>ISO 9001:2015</c>.</summary>
    Enumeration = 3,
}

/// <summary>Which real-world entity a field is expected to name, if any.</summary>
public enum EntityMatchTarget
{
    None = 0,

    /// <summary>Must be the supplier — the certificate holder (SRS §8.3 <c>holderName</c>).</summary>
    SupplierName = 1,

    /// <summary>Must be one of the requirement's accepted issuers, when it constrains them.</summary>
    AcceptedIssuer = 2,
}

/// <summary>
/// One field in a document type's extraction contract (SRS §8.3).
/// <para>
/// Declarative on purpose: the same definition drives the prompt, the JSON schema handed to the
/// model as a structured-output constraint, and the validators that score the result. Adding a
/// document type is adding one of these documents, not writing code (FR-3.9).
/// </para>
/// </summary>
public sealed record FieldDefinition
{
    /// <summary>
    /// Patterns arrive from configuration, so a pathological one must not be able to hang a
    /// worker. A per-match timeout is cheaper than auditing every regex a future document type
    /// brings with it.
    /// </summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public FieldDefinition(
        string name,
        FieldValueType valueType,
        bool isMandatory,
        string? pattern = null,
        IReadOnlyList<string>? allowedValues = null,
        EntityMatchTarget entityMatch = EntityMatchTarget.None)
    {
        Name = Guard.AgainstNullOrWhiteSpace(name, "intelligence.field_definition.name_required");
        ValueType = valueType;
        IsMandatory = isMandatory;
        Pattern = pattern;
        AllowedValues = allowedValues ?? [];
        EntityMatch = entityMatch;

        Guard.Require(
            valueType != FieldValueType.Enumeration || AllowedValues.Count > 0,
            "intelligence.field_definition.enumeration_needs_values",
            $"Field '{name}' is an enumeration but declares no allowed values.");

        if (pattern is not null)
        {
            // Fail at schema-load time rather than mid-extraction on a malformed pattern.
            _ = new Regex(pattern, RegexOptions.None, PatternTimeout);
        }
    }

    public string Name { get; }

    public FieldValueType ValueType { get; }

    /// <summary>
    /// A mandatory field must be attempted before a job can complete, and it is what the
    /// worst-field rule folds over when deriving overall confidence (SRS §8.4).
    /// </summary>
    public bool IsMandatory { get; }

    /// <summary>Optional regex the raw value must match, where the format is known.</summary>
    public string? Pattern { get; }

    public IReadOnlyList<string> AllowedValues { get; }

    public EntityMatchTarget EntityMatch { get; }

    public bool MatchesPattern(string value) =>
        Pattern is null || Regex.IsMatch(value, Pattern, RegexOptions.None, PatternTimeout);
}

/// <summary>
/// The full extraction contract for one document type. Loaded from versioned JSON by
/// Infrastructure; the domain only ever sees this shape.
/// </summary>
public sealed class DocumentTypeSchema
{
    public DocumentTypeSchema(
        string documentType,
        string schemaVersion,
        IReadOnlyCollection<FieldDefinition> fields)
    {
        DocumentType = Guard.AgainstNullOrWhiteSpace(documentType, "intelligence.schema.document_type_required");
        SchemaVersion = Guard.AgainstNullOrWhiteSpace(schemaVersion, "intelligence.schema.version_required");

        Guard.AgainstNull(fields, "intelligence.schema.fields_required");

        Guard.Require(
            fields.Count > 0,
            "intelligence.schema.no_fields",
            $"Document type '{documentType}' declares no fields.");

        var duplicates = fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Guard.Require(
            duplicates.Count == 0,
            "intelligence.schema.duplicate_field",
            $"Duplicate field names in '{documentType}': {string.Join(", ", duplicates)}.");

        Guard.Require(
            fields.Any(f => f.IsMandatory),
            "intelligence.schema.no_mandatory_fields",
            $"Document type '{documentType}' has no mandatory fields, so no extraction could ever fail.");

        Fields = [.. fields];
    }

    public string DocumentType { get; }

    /// <summary>
    /// Recorded on every job alongside the model and prompt version (FR-3.8), so a change in
    /// extraction quality can be traced to the change that caused it.
    /// </summary>
    public string SchemaVersion { get; }

    public IReadOnlyList<FieldDefinition> Fields { get; }

    public IEnumerable<FieldDefinition> MandatoryFields => Fields.Where(f => f.IsMandatory);

    public FieldDefinition? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

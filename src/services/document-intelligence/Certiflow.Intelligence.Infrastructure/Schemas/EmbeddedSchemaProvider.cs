using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Certiflow.Intelligence.Application.Abstractions;
using Certiflow.Intelligence.Domain.Schemas;

namespace Certiflow.Intelligence.Infrastructure.Schemas;

/// <summary>
/// Loads document-type extraction contracts from JSON embedded in this assembly.
/// <para>
/// Schemas are configuration versioned with the code (SRS §13.2). Adding a document type is adding
/// a JSON file — no code changes anywhere, which is what FR-3.9 asks for. Blob-hosted schemas are
/// the obvious later step and would replace only this class.
/// </para>
/// <para>
/// One schema can serve several document types: an ISO 14001 certificate has the same fields as an
/// ISO 9001 one, so <c>appliesTo</c> lets a contract be reused rather than copy-pasted.
/// </para>
/// </summary>
public sealed class EmbeddedSchemaProvider : IDocumentTypeSchemaProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ConcurrentDictionary<string, DocumentTypeSchema> _schemas;

    public EmbeddedSchemaProvider()
    {
        _schemas = new ConcurrentDictionary<string, DocumentTypeSchema>(StringComparer.OrdinalIgnoreCase);

        var assembly = Assembly.GetExecutingAssembly();

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded schema '{name}' could not be opened.");

            var definition = JsonSerializer.Deserialize<SchemaFile>(stream, Json)
                ?? throw new InvalidOperationException($"Embedded schema '{name}' is empty.");

            var schema = definition.ToDomain();

            // Registered under its own document type and any it also covers.
            _schemas[schema.DocumentType] = schema;

            foreach (var alias in definition.AppliesTo ?? [])
            {
                _schemas[alias] = new DocumentTypeSchema(alias, schema.SchemaVersion, schema.Fields);
            }
        }
    }

    public IReadOnlyList<string> KnownDocumentTypes => [.. _schemas.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    public DocumentTypeSchema? Find(string documentType) =>
        string.IsNullOrWhiteSpace(documentType) ? null : _schemas.GetValueOrDefault(documentType);

    private sealed record SchemaFile(
        string DocumentType,
        string SchemaVersion,
        IReadOnlyList<FieldFile> Fields,
        IReadOnlyList<string>? AppliesTo = null)
    {
        public DocumentTypeSchema ToDomain() =>
            new(DocumentType, SchemaVersion, [.. Fields.Select(field => field.ToDomain())]);
    }

    private sealed record FieldFile(
        string Name,
        FieldValueType ValueType,
        bool IsMandatory,
        string? Pattern = null,
        IReadOnlyList<string>? AllowedValues = null,
        EntityMatchTarget EntityMatch = EntityMatchTarget.None)
    {
        public FieldDefinition ToDomain() =>
            new(Name, ValueType, IsMandatory, Pattern, AllowedValues, EntityMatch);
    }
}

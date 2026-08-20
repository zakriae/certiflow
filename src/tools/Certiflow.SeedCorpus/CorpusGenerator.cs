using System.Text.Json;
using System.Text.Json.Serialization;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Certiflow.SeedCorpus;

/// <summary>
/// Writes the corpus to disk: one PDF per certificate plus a <c>manifest.json</c> describing
/// everything, which is what the database seeder consumes.
/// </summary>
public static class CorpusGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Generates the whole corpus into <paramref name="outputDirectory"/>, replacing whatever was
    /// there before. Returns the manifest so callers - including tests - can assert on it without
    /// re-reading the file.
    /// </summary>
    public static SeedCorpusManifest Generate(string outputDirectory, DateOnly referenceDate, int seed = 20260818)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        // QuestPDF is dual-licensed and requires the licence to be selected explicitly. The
        // Community terms cover this project; asserting it here rather than in Program.cs means
        // tests exercise the same path the tool does.
        QuestPDF.Settings.License = LicenseType.Community;

        var certificatesDirectory = Path.Combine(outputDirectory, "certificates");
        Directory.CreateDirectory(certificatesDirectory);

        foreach (var stale in Directory.EnumerateFiles(certificatesDirectory, "*.pdf"))
        {
            File.Delete(stale);
        }

        var manifest = new SeedCorpusManifest(
            referenceDate,
            seed,
            CorpusDefinition.Categories,
            CorpusDefinition.Suppliers(referenceDate));

        foreach (var certificate in manifest.Suppliers.SelectMany(supplier => supplier.Certificates))
        {
            var path = Path.Combine(certificatesDirectory, certificate.FileName);
            new CertificateDocument(certificate).GeneratePdf(path);
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        return manifest;
    }

    /// <summary>
    /// Every field value the extraction pipeline will be asked to read back out of a certificate.
    /// <para>
    /// Used by the corpus tests to prove each one is present verbatim in the generated text layer.
    /// If a value here cannot be located in the PDF, grounding can never verify it and the whole
    /// confidence mechanism reports zero for a document that is perfectly correct.
    /// </para>
    /// </summary>
    public static IEnumerable<(string Field, string Value)> ExtractableValues(SeedCertificate certificate)
    {
        yield return (CertificateFields.HolderName, certificate.HolderName);
        yield return (CertificateFields.IssuerName, certificate.IssuerName);
        yield return (CertificateFields.CertificateNumber, certificate.CertificateNumber);
        yield return (CertificateFields.Scope, certificate.Scope);

        if (certificate.Standard is not null)
        {
            yield return (CertificateFields.Standard, certificate.Standard);
        }
    }
}

/// <summary>Field names shared with the extraction schema of SRS §8.3.</summary>
public static class CertificateFields
{
    public const string HolderName = "holderName";
    public const string IssuerName = "issuerName";
    public const string CertificateNumber = "certificateNumber";
    public const string IssuedOn = "issuedOn";
    public const string ExpiresOn = "expiresOn";
    public const string Scope = "scope";
    public const string Standard = "standard";
}

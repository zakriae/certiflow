using System.Globalization;
using Azure.AI.OpenAI;
using Azure.Identity;
using Certiflow.Intelligence.Application.Extraction;
using Certiflow.Intelligence.Domain;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.Intelligence.Infrastructure.Ai;
using Certiflow.Intelligence.Infrastructure.Parsing;
using Certiflow.Intelligence.Infrastructure.Schemas;
using Certiflow.SeedCorpus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Usage: dotnet run --project src/tools/Certiflow.ExtractionSpike -- [--endpoint <uri>] [--deployment <name>] [--take <n>]
//
// Authenticates with DefaultAzureCredential, so locally this uses your `az login` session and
// there is no API key anywhere (NFR-9).

var endpoint = new Uri(Args.Value("--endpoint") ?? "https://certiflow-openai-zk.openai.azure.com/");
var deployment = Args.Value("--deployment") ?? "gpt-5-mini";
var take = int.TryParse(Args.Value("--take"), out var parsedTake) ? parsedTake : 3;

Console.WriteLine($"Endpoint   : {endpoint}");
Console.WriteLine($"Deployment : {deployment}");
Console.WriteLine();

// ── Generate the corpus, then extract from it ───────────────────────────────────────────────────

var today = DateOnly.FromDateTime(DateTime.UtcNow);
var corpusDirectory = Path.Combine(Path.GetTempPath(), "certiflow-spike-corpus");
var manifest = CorpusGenerator.Generate(corpusDirectory, today);

var schemas = new EmbeddedSchemaProvider();
var parser = new PdfPigDocumentTextParser();

// The service API version is pinned rather than left to default. The Azure shim chooses how to
// serialise the output-token cap based on it, and older versions emit `max_tokens`, which every
// gpt-5-generation model rejects with HTTP 400.
var clientOptions = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_04_01_Preview);

var extractor = new AzureOpenAIFieldExtractor(
    new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), clientOptions),
    Options.Create(new AzureOpenAIOptions { Endpoint = endpoint, DeploymentName = deployment }),
    NullLogger<AzureOpenAIFieldExtractor>.Instance);

var pipeline = new ExtractionPipeline(parser, extractor, NullLogger<ExtractionPipeline>.Instance);

// Only ISO certificates: the schema provider ships one contract so far, and asking it to extract
// a document type it has no contract for would prove nothing.
var only = Args.Value("--only");

var candidates = manifest.Suppliers
    .SelectMany(supplier => supplier.Certificates.Select(certificate => (supplier, certificate)))
    .Where(pair => schemas.Find(pair.certificate.DocumentType) is not null)
    .Where(pair => only is null
                || pair.supplier.LegalName.Contains(only, StringComparison.OrdinalIgnoreCase))
    .Take(take)
    .ToList();

if (candidates.Count == 0)
{
    Console.WriteLine(only is null
        ? "No certificates matched a known document-type schema."
        : $"No certificates for a supplier matching \"{only}\".");

    return 1;
}

var totalTokens = 0;

foreach (var (supplier, certificate) in candidates)
{
    var schema = schemas.Find(certificate.DocumentType)!;
    var requirement = CorpusDefinition.Categories
        .Single(category => category.CategoryId == supplier.CategoryId)
        .Requirements.Single(r => r.RequirementId == certificate.RequirementId);

    var request = new ExtractionRequest(
        new DocumentId(certificate.DocumentId),
        new SupplierId(supplier.SupplierId),
        new RequirementId(certificate.RequirementId),
        schema,
        new ExtractionContext(
            supplierLegalName: supplier.LegalName,
            supplierTradingName: supplier.TradingName,
            acceptedIssuers: requirement.AcceptedIssuers,
            requiresIssuerMatch: requirement.RequiresIssuerMatch,
            expectedStandard: certificate.Standard,
            today: today),
        Confidence.FromScore(0.85m));

    var path = Path.Combine(corpusDirectory, "certificates", certificate.FileName);
    await using var stream = File.OpenRead(path);

    Report.Header(supplier.LegalName, certificate);

    try
    {
        var outcome = await pipeline.RunAsync(request, stream, CancellationToken.None);

        totalTokens += outcome.Job.TokensConsumed;
        Report.Fields(outcome, certificate, supplier.LegalName);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"  FAILED: {exception.GetType().Name}: {exception.Message}");
    }

    Console.WriteLine();
}

// ── The negative control ────────────────────────────────────────────────────────────────────────
// Everything above could pass against a grounding check that returned Verified unconditionally.
// This proves it can still fail: a citation that is not in the document must score exactly 0.00.

Console.WriteLine(new string('─', 100));
Console.WriteLine("NEGATIVE CONTROL — a fabricated citation");
Console.WriteLine();

var (probeSupplier, probeCertificate) = candidates[0];
var probePath = Path.Combine(corpusDirectory, "certificates", probeCertificate.FileName);
await using (var probeStream = File.OpenRead(probePath))
{
    var parsed = await parser.ParseAsync(probeStream, CancellationToken.None);

    var honest = GroundingVerifier.Verify(new Citation(1, probeCertificate.CertificateNumber), parsed);
    var invented = GroundingVerifier.Verify(new Citation(1, "Certificate No. ZZ-FAKE-999999"), parsed);

    var honestScore = ConfidenceCalculator.Compute(
    [
        SignalOutcome.Pass(ConfidenceSignal.Grounding),
        SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
    ]);

    var inventedScore = ConfidenceCalculator.Compute(
    [
        SignalOutcome.Fail(ConfidenceSignal.Grounding, invented.Detail),
        SignalOutcome.Pass(ConfidenceSignal.TypeValidity),
    ]);

    Console.WriteLine($"  real value  \"{probeCertificate.CertificateNumber}\"");
    Console.WriteLine($"    grounded  {honest.Result}   confidence {honestScore.Confidence}");
    Console.WriteLine("  fabricated  \"Certificate No. ZZ-FAKE-999999\"");
    Console.WriteLine($"    grounded  {invented.Result}   confidence {inventedScore.Confidence}   vetoed: {inventedScore.GroundingVetoed}");
    Console.WriteLine();
    Console.WriteLine(inventedScore.Confidence.Value == 0m && honestScore.Confidence.Value > 0m
        ? "  PASS — a value that is not in the document scores 0.00, however well-formed it looks."
        : "  FAIL — the grounding veto did not fire.");
}

Console.WriteLine();
Console.WriteLine(new string('─', 100));
Console.WriteLine($"Documents extracted: {candidates.Count}   total tokens: {totalTokens}");
Console.WriteLine($"Corpus: {corpusDirectory}");

return 0;

internal static class Args
{
    public static string? Value(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal static class Report
{
    public static void Header(string supplierName, SeedCertificate certificate)
    {
        Console.WriteLine(new string('─', 100));
        Console.WriteLine($"{supplierName}  ·  {certificate.DocumentType}  ·  {certificate.Layout}  ·  {certificate.Language}");

        if (certificate.DemoNote is not null)
        {
            Console.WriteLine($"  note: {certificate.DemoNote}");
        }

        Console.WriteLine();
    }

    public static void Fields(ExtractionOutcome outcome, SeedCertificate expected, string supplierLegalName)
    {
        Console.WriteLine($"  {"FIELD",-20} {"CONF",-6} {"GROUNDED",-16} {"PAGE",-5} VALUE");

        foreach (var field in outcome.Fields.OrderBy(f => f.FieldName, StringComparer.Ordinal))
        {
            var page = field.Citation?.PageNumber.ToString(CultureInfo.InvariantCulture) ?? "-";
            var value = field.TypedValue ?? field.RawValue ?? "(not returned)";

            if (value.Length > 44)
            {
                value = value[..41] + "...";
            }

            Console.WriteLine(
                $"  {field.FieldName,-20} {field.Confidence,-6} {field.GroundingResult,-16} {page,-5} {value}");
        }

        Console.WriteLine();
        Console.WriteLine($"  overall confidence : {outcome.Job.OverallConfidence}");
        Console.WriteLine($"  auto-acceptable    : {outcome.Job.IsAutoAcceptable}");
        Console.WriteLine($"  tokens             : {outcome.Job.TokensConsumed}");

        // The corpus knows the right answers, so the spike can check them rather than just display.
        var holder = outcome.Fields.SingleOrDefault(f => f.FieldName == "holderName");

        if (holder?.RawValue is not null)
        {
            var matchesDocument = string.Equals(holder.RawValue.Trim(), expected.HolderName, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  holder read back   : {(matchesDocument ? "matches the document" : $"MISREAD — expected \"{expected.HolderName}\"")}");

            if (!string.Equals(expected.HolderName, supplierLegalName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  entity match       : supplier is \"{supplierLegalName}\" — mismatch is expected here");
            }
        }
    }
}

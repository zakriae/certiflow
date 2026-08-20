using Certiflow.SeedCorpus;

// Usage: dotnet run -- [--output <dir>] [--date yyyy-MM-dd] [--seed <int>]
//
// --date exists so the corpus can be regenerated for a fixed reference date, which is what makes
// tests and screenshots reproducible. Without it the corpus is always relative to today, which is
// what you want before a recording and not what you want in a test.

var output = ArgumentValue("--output") ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "seed-corpus");
var seed = int.TryParse(ArgumentValue("--seed"), out var parsedSeed) ? parsedSeed : 20260818;

var referenceDate = DateOnly.TryParse(ArgumentValue("--date"), out var parsedDate)
    ? parsedDate
    : DateOnly.FromDateTime(DateTime.UtcNow);

var manifest = CorpusGenerator.Generate(output, referenceDate, seed);

var certificates = manifest.Suppliers.Sum(supplier => supplier.Certificates.Count);

Console.WriteLine($"Seed corpus written to {Path.GetFullPath(output)}");
Console.WriteLine($"  reference date : {referenceDate:yyyy-MM-dd}");
Console.WriteLine($"  categories     : {manifest.Categories.Count}");
Console.WriteLine($"  suppliers      : {manifest.Suppliers.Count}");
Console.WriteLine($"  certificates   : {certificates}");
Console.WriteLine();
Console.WriteLine("Designed compliance spread:");

foreach (var group in manifest.Suppliers.GroupBy(supplier => supplier.ExpectedStatus).OrderBy(group => group.Key))
{
    Console.WriteLine($"  {group.Key,-14} {group.Count()}");
}

Console.WriteLine();
Console.WriteLine("Cases the demo turns on:");

foreach (var supplier in manifest.Suppliers.Where(s => s.DemoRole.Contains("AT RISK", StringComparison.Ordinal)
                                                    || s.DemoRole.Contains("NON-COMPLIANT", StringComparison.Ordinal)
                                                    || s.DemoRole.Contains("AWAITING REVIEW", StringComparison.Ordinal)
                                                    || s.DemoRole.Contains("SUSPENDED", StringComparison.Ordinal)))
{
    Console.WriteLine($"  {supplier.LegalName,-28} {supplier.DemoRole}");
}

return 0;

string? ArgumentValue(string name)
{
    var index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

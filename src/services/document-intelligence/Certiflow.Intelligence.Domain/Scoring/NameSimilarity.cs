using Certiflow.Intelligence.Domain.Grounding;

namespace Certiflow.Intelligence.Domain.Scoring;

/// <summary>
/// Compares an organisation name on a certificate against the name on record (SRS §8.3, the
/// <c>holderName</c> and <c>issuerName</c> checks).
/// <para>
/// Exact matching is useless here — real certificates say "Meridian Logistics" where the register
/// says "Meridian Logistics SARL", and neither is wrong. So legal-form suffixes are stripped and
/// the remainder is scored. Deliberately <em>not</em> generous beyond that: "Meridian Logistics
/// Group" is a different legal entity from "Meridian Logistics SARL", and a check that waves that
/// through is worse than no check, because it produces a green tick on the exact failure mode
/// the product exists to catch.
/// </para>
/// <para>
/// Everything here is pure and deterministic, which is the whole reason confidence can be
/// computed rather than asked for.
/// </para>
/// </summary>
public static class NameSimilarity
{
    /// <summary>SRS §8.3: fuzzy match at or above this counts as a match.</summary>
    public const decimal MatchThreshold = 0.85m;

    /// <summary>
    /// Legal-form suffixes carry no identifying information, and whether a certificate prints
    /// them is a formatting accident. FR and EN forms, per the language scope in SRS §1.4.
    /// </summary>
    private static readonly HashSet<string> LegalFormTokens = new(StringComparer.Ordinal)
    {
        "sarl", "sas", "sasu", "sa", "eurl", "sci", "snc", "scop", "gie",
        "ltd", "limited", "llc", "llp", "lp", "plc", "inc", "incorporated", "corp", "corporation",
        "gmbh", "ag", "bv", "nv", "ab", "as", "oy", "aps", "kft", "sp", "zoo",
        "srl", "spa", "sl", "pty", "co", "company",
    };

    /// <summary>
    /// Returns a similarity in [0,1]. Compares against every name on record and keeps the best —
    /// a certificate may legitimately use a trading name rather than the legal name.
    /// </summary>
    public static decimal Best(string? candidate, params string?[] knownNames) =>
        BestOf(candidate, knownNames);

    /// <summary>Collection overload, for issuer lists that arrive from a requirement.</summary>
    public static decimal BestOf(string? candidate, IEnumerable<string?> knownNames)
    {
        var best = 0m;

        foreach (var known in knownNames)
        {
            best = Math.Max(best, Score(candidate, known));
        }

        return best;
    }

    public static decimal Score(string? candidate, string? known)
    {
        var a = Canonicalize(candidate);
        var b = Canonicalize(known);

        if (a.Length == 0 || b.Length == 0)
        {
            return 0m;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1m;
        }

        // Two independent views of "how close": character edits catch typos and OCR slips, token
        // overlap catches reordering and extra words. The better of the two wins, so neither
        // weakness dominates — but no containment shortcut, which is what keeps an added word
        // like "Group" from scoring as a match.
        return Math.Max(CharacterRatio(a, b), TokenJaccard(a, b));
    }

    public static bool IsMatch(string? candidate, params string?[] knownNames) =>
        Best(candidate, knownNames) >= MatchThreshold;

    /// <summary>
    /// Normalises casing, diacritics and punctuation, then drops legal-form tokens. Reuses
    /// <see cref="TextNormalizer"/> so name matching and grounding agree on what two strings
    /// being "the same text" means.
    /// </summary>
    private static string Canonicalize(string? value)
    {
        var normalized = TextNormalizer.Normalize(value);

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var tokens = normalized
            .Split([' ', ',', '.', '-', '/', '&', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Single characters carry no identifying information and are almost always punctuation
            // artefacts — "S.A.R.L." splits into four of them and must reduce to nothing, so that
            // it compares equal to the same name printed "SARL".
            .Where(t => t.Length > 1 && !LegalFormTokens.Contains(t))
            .ToList();

        // Stripping everything means the name was nothing but a legal form; fall back to the
        // normalised original rather than returning an empty string that matches anything.
        return tokens.Count > 0 ? string.Join(' ', tokens) : normalized;
    }

    private static decimal CharacterRatio(string a, string b)
    {
        var distance = LevenshteinDistance(a, b);
        var longest = Math.Max(a.Length, b.Length);

        return longest == 0 ? 1m : 1m - ((decimal)distance / longest);
    }

    private static decimal TokenJaccard(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0m;
        }

        var intersection = setA.Intersect(setB, StringComparer.Ordinal).Count();
        var union = setA.Count + setB.Count - intersection;

        return (decimal)intersection / union;
    }

    /// <summary>Two-row Levenshtein — O(min(n,m)) memory, which is all this needs.</summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length < b.Length)
        {
            (a, b) = (b, a);
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

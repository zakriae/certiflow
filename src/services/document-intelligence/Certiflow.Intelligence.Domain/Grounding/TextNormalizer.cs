using System.Globalization;
using System.Text;

namespace Certiflow.Intelligence.Domain.Grounding;

/// <summary>
/// Puts model output and parsed PDF text into the same shape so they can be compared verbatim.
/// <para>
/// Without this, grounding fails constantly for uninteresting reasons: a PDF text layer emits
/// <c>ﬁ</c> as a ligature, breaks a line mid-phrase, uses a non-breaking space between a number
/// and its unit, or renders an en dash where the model returned a hyphen. Every one of those is
/// a false negative, and a grounding check with false negatives sends good documents to a human
/// reviewer — which destroys the auto-accept path the product's value rests on.
/// </para>
/// <para>
/// Everything here is deterministic and reversible in meaning, never in content: nothing is
/// dropped that could change which certificate a snippet refers to. Digits, letters and their
/// order all survive.
/// </para>
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Characters PDF producers use interchangeably with their ASCII equivalents. Mapped before
    /// diacritic folding so the decomposition step sees plain forms.
    /// </summary>
    private static readonly Dictionary<char, string> Replacements = new()
    {
        // Dashes and hyphens: certificate numbers are full of these.
        ['‐'] = "-", ['‑'] = "-", ['‒'] = "-", ['–'] = "-",
        ['—'] = "-", ['―'] = "-", ['−'] = "-",

        // Quotes and apostrophes: French text ("l'organisme") is full of these.
        ['‘'] = "'", ['’'] = "'", ['‚'] = "'", ['‛'] = "'",
        ['“'] = "\"", ['”'] = "\"", ['„'] = "\"", ['«'] = "\"",
        ['»'] = "\"", ['‹'] = "'", ['›'] = "'",

        // Ligatures that PDF text layers emit as single glyphs.
        ['ﬀ'] = "ff", ['ﬁ'] = "fi", ['ﬂ'] = "fl",
        ['ﬃ'] = "ffi", ['ﬄ'] = "ffl", ['œ'] = "oe", ['Œ'] = "OE",
        ['æ'] = "ae", ['Æ'] = "AE", ['ß'] = "ss",

        // Punctuation lookalikes.
        ['…'] = "...", [' '] = " ", [' '] = " ", [' '] = " ",
        ['​'] = "", ['‌'] = "", ['‍'] = "", ['﻿'] = "",
    };

    /// <summary>
    /// Casefolds, folds diacritics, canonicalises punctuation and collapses all whitespace runs
    /// to a single space. Returns an empty string for null or blank input rather than throwing —
    /// a missing snippet is a grounding failure, not an exception.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var substituted = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (Replacements.TryGetValue(c, out var replacement))
            {
                substituted.Append(replacement);
            }
            else
            {
                substituted.Append(c);
            }
        }

        // FormD splits "é" into "e" + combining acute, so the combining mark can be dropped.
        // This is why the build does not enable InvariantGlobalization — see Directory.Build.props.
        var decomposed = substituted.ToString().Normalize(NormalizationForm.FormD);

        var result = new StringBuilder(decomposed.Length);
        var lastWasSpace = true; // leading whitespace is skipped

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    result.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            result.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }

        // Recompose so the result is a stable, comparable string rather than a decomposed one.
        return result.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }
}

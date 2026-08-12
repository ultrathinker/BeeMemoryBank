using System.Globalization;
using System.Text;

namespace BeeMemoryBank.Search;

/// <summary>
/// Shared Unicode normalization used by <see cref="ITokenizer"/> implementations and by callers
/// that need to normalize a single already-extracted token the same way a full tokenization pass
/// would (e.g. to state "stem is a prefix of the normalized token" precisely in tests).
/// </summary>
public static class TextNormalization
{
    /// <summary>
    /// Applies NFKC (compatibility) normalization, culture-invariant lowercasing, and diacritic
    /// stripping (e.g. <c>café</c> → <c>cafe</c>), in that order. Does not split on whitespace or
    /// punctuation — see <see cref="DefaultTokenizer"/> for that. Never throws; <c>null</c> or
    /// empty input yields an empty string.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // NFKC first: folds compatibility characters (full-width forms, ligatures, etc.) to their
        // canonical equivalents before we reason about individual characters below.
        string normalized = input.Normalize(NormalizationForm.FormKC);
        string lowered = normalized.ToLowerInvariant();

        // Diacritic stripping: decompose into base character + combining marks (NFD), then drop
        // the marks. This turns "café" into "cafe" without needing a per-character lookup table.
        // Note this also folds Cyrillic "ё" (U+0451) to "е" (U+0435) + a combining diaeresis that
        // gets stripped, which mirrors the common informal Russian spelling convention.
        string decomposed = lowered.Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}

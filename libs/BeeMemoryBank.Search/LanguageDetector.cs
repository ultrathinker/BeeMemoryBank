namespace BeeMemoryBank.Search;

/// <summary>
/// Classifies a token's dominant alphabet using simple per-character Unicode-range checks — no
/// statistical model, no dictionary lookup. Deliberately coarse: this only needs to route a token
/// to the right stemmer (or skip it), not identify a language with linguistic rigor.
/// </summary>
public static class LanguageDetector
{
    /// <summary>
    /// Detects the dominant alphabet of <paramref name="token"/> by counting Cyrillic vs. Latin
    /// letters. Digits, punctuation, symbols, and emoji don't count toward either side. Ties
    /// (including 0-0, e.g. an all-digit token, and genuinely mixed-script tokens with an equal
    /// split) resolve to <see cref="DetectedLanguage.Unknown"/>. Never throws, including for
    /// <c>null</c> or empty input.
    /// </summary>
    public static DetectedLanguage Detect(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return DetectedLanguage.Unknown;
        }

        int cyrillicCount = 0;
        int latinCount = 0;

        foreach (char c in token)
        {
            if (IsCyrillic(c))
            {
                cyrillicCount++;
            }
            else if (IsLatin(c))
            {
                latinCount++;
            }
        }

        if (cyrillicCount > latinCount)
        {
            return DetectedLanguage.Russian;
        }

        if (latinCount > cyrillicCount)
        {
            return DetectedLanguage.English;
        }

        return DetectedLanguage.Unknown;
    }

    // Main Cyrillic block: covers the Russian alphabet (а-я, А-Я) plus ё/Ё (U+0451/U+0401) and a
    // handful of other Slavic letters. Good enough for "is this word Russian-ish" without a
    // dictionary.
    private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    // Basic Latin letters plus the Latin-1 Supplement / Latin Extended-A/B ranges, so this still
    // recognizes accented Latin letters when called on text before diacritic stripping, even
    // though DefaultTokenizer already strips diacritics before language detection normally runs.
    private static bool IsLatin(char c) =>
        c is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= 'À' and <= 'ɏ';
}

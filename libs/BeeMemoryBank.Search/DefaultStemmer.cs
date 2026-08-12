namespace BeeMemoryBank.Search;

/// <summary>
/// Dispatches a normalized token to the Russian or English truncation-only stemmer based on
/// <see cref="LanguageDetector"/>'s classification. Tokens classified as
/// <see cref="DetectedLanguage.Unknown"/> (pure digits, punctuation remnants, emoji/symbols,
/// evenly mixed-script garbage) pass through unchanged — never crashes on any input.
/// </summary>
/// <remarks>
/// <b>Why this re-detects language in a loop instead of once.</b> Each individual language
/// stemmer already strips to its own fixed point internally (see <see cref="SuffixStripper"/>),
/// which guarantees <c>EnglishStemmer.Stem(EnglishStemmer.Stem(x)) == EnglishStemmer.Stem(x)</c>
/// (and likewise for Russian) on its own. But dispatch adds a second moving part: stripping
/// characters can change which script is dominant in what's left. A mixed-script token like
/// "sвetйed" (4 Latin, 3 Cyrillic — English-dominant) has its English "-ed" suffix stripped to
/// "sвети", which is now Cyrillic-dominant; stemming that as Russian strips its trailing case
/// vowel too. If <see cref="Stem"/> only detected language once per call, calling it a second
/// time on that already-partially-stemmed result would detect Russian from the start and strip
/// further — violating the idempotency guarantee documented on <see cref="IStemmer"/>. Looping
/// re-detection-and-strip to a fixed point within a single <see cref="Stem"/> call closes that
/// gap the same way <see cref="SuffixStripper"/> closes it for individual suffix rules: once this
/// method returns, a full detect-and-strip cycle changed nothing, so calling it again immediately
/// hits that same "nothing changed" outcome on its very first cycle.
/// </remarks>
public sealed class DefaultStemmer : IStemmer
{
    private readonly EnglishStemmer _english = new();
    private readonly RussianStemmer _russian = new();

    /// <inheritdoc />
    public string Stem(string? normalizedToken)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return normalizedToken ?? string.Empty;
        }

        string current = normalizedToken;

        // Bounded by the token's own length: every iteration that changes `current` strictly
        // shortens it, so this can run at most once per character before the "no change" exit
        // condition below is forced to trigger. In the overwhelmingly common case (a token whose
        // dominant script never flips while being stemmed) this loop body runs exactly twice: once
        // to do the real work, once to confirm nothing more applies.
        for (int i = 0; i <= current.Length; i++)
        {
            string next = LanguageDetector.Detect(current) switch
            {
                DetectedLanguage.Russian => _russian.Stem(current),
                DetectedLanguage.English => _english.Stem(current),
                _ => current,
            };

            if (next == current)
            {
                return current;
            }

            current = next;
        }

        return current;
    }
}

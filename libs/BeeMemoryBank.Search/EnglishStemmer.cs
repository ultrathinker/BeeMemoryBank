namespace BeeMemoryBank.Search;

/// <summary>
/// Truncation-only English stemmer, inspired by Porter/Porter2's suffix tables but restricted so
/// every rule only ever deletes a trailing substring — never substitutes it (see
/// <see cref="IStemmer"/> for why that restriction exists). This intentionally departs from
/// textbook Porter2 in several places; see the file-level remarks below for exactly which rules
/// were skipped or reshaped and why.
/// </summary>
/// <remarks>
/// <para><b>Deliberate divergences from textbook Porter/Porter2:</b></para>
/// <list type="bullet">
/// <item>
/// <b>No consonant+"y" -&gt; "i" substitution</b> (Porter: <c>happy</c> -&gt; <c>happi</c> before
/// adding <c>-er</c>/<c>-ly</c>/<c>-ness</c>). That step replaces a character, not just deletes
/// one, so it is dropped entirely. Trailing "-y" is left alone; words like "happier"/"happiest"
/// are still handled by the plain <c>-er</c>/<c>-est</c> deletion rules below (giving "happi" as
/// the stem rather than Porter's "happy" — a known, accepted quality loss).
/// </item>
/// <item>
/// <b>No consonant-doubling undo</b> (Porter: <c>running</c> -&gt; <c>run</c> by dropping "-ing"
/// and then one of the doubled "n"s). Undoing the double is itself a pure deletion and would be
/// safe to add, but it requires detecting "the stem now ends in a doubled consonant" as a special
/// case beyond a fixed suffix table, which is the generic-rule-engine complexity this WP's brief
/// asked to avoid. Skipped: "running" stems to "runn", not "run".
/// </item>
/// <item>
/// <b>"-tion"/"-sion" collapsed into a single generic "-ion" (3 chars) rule</b> instead of two
/// 4-char rules. Deleting just "-ion" keeps one more character of the root than deleting
/// "-tion"/"-sion" would (e.g. "creation" -&gt; "creat" instead of "crea"; "vision" -&gt; "vis"
/// instead of being left unstemmed by the length guard), which is strictly better recall/quality
/// for the same deletion-only property, so the shorter generic rule was chosen instead of the two
/// textbook ones.
/// </item>
/// <item>
/// <b>"-es" only strips after a sibilant</b> (preceding letter is <c>s</c>, <c>x</c>, <c>z</c>, or
/// <c>h</c>, covering "-ch"/"-sh"), matching the actual English orthography rule for when "-es"
/// (rather than plain "-s") forms the plural ("boxes", "watches", "buses" strip to "box", "watch",
/// "bus"). Without that guard, silent-e root words like "codes"/"bikes"/"names" would incorrectly
/// lose the root's own trailing "e" (giving "cod"/"bik"/"nam"); they now fall through to the plain
/// "-s" rule instead and stem to "code"/"bike"/"name".
/// </item>
/// <item>
/// <b>Plain "-s" refuses to strip after "-ss", "-us", "-is"</b> (e.g. "glass", "bus", "this") —
/// a deletion-only analogue of Porter's guard against over-stripping short, non-plural words. This
/// guard also protects idempotency for compound words that happen to end in "ss" after an earlier
/// strip in the same pass (see <see cref="SuffixStripper"/>).
/// </item>
/// </list>
/// <para>
/// All remaining suffixes (-ing, -ed, -er, -est, -ly, -ness, -ment, plain -s/-es) are deleted
/// outright with only a minimum-remaining-length guard, exactly as the brief asks: "reasonable
/// recall over perfect precision".
/// </para>
/// </remarks>
public sealed class EnglishStemmer : IStemmer
{
    private const int MinRemainingLength = 3;

    // Sorted longest-suffix-first: SuffixStripper tries these in order and stops at the first
    // suffix that both matches and satisfies its guard, so a 4-char match is always preferred
    // over a shorter one that happens to also match the tail of the same word.
    private static readonly SuffixRule[] Rules =
    [
        new SuffixRule("ness", MinRemainingLength),
        new SuffixRule("ment", MinRemainingLength),
        new SuffixRule("ing", MinRemainingLength),
        new SuffixRule("ion", MinRemainingLength),
        new SuffixRule("est", MinRemainingLength),
        new SuffixRule("ed", MinRemainingLength),
        new SuffixRule("er", MinRemainingLength),
        new SuffixRule("es", MinRemainingLength, IsSibilantPlural),
        new SuffixRule("ly", MinRemainingLength),
        new SuffixRule("s", MinRemainingLength, IsPlainPluralS),
    ];

    /// <inheritdoc />
    public string Stem(string? normalizedToken)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return normalizedToken ?? string.Empty;
        }

        return SuffixStripper.StripToFixedPoint(normalizedToken, Rules);
    }

    // "-es" only forms a plural by itself after a sibilant/affricate ending (s, x, z, or the
    // second letter of "ch"/"sh"); otherwise the "e" belongs to the root (codes, bikes, names).
    private static bool IsSibilantPlural(string word, int len)
    {
        char before = word[len - 3];
        return before is 's' or 'x' or 'z' or 'h';
    }

    // Refuse to strip a bare "-s" off words ending in "-ss"/"-us"/"-is" (glass, bus, this) to
    // avoid mangling short, already-singular words.
    private static bool IsPlainPluralS(string word, int len)
    {
        char before = word[len - 2];
        return before is not ('s' or 'u' or 'i');
    }
}

namespace BeeMemoryBank.Search;

/// <summary>
/// A single deletion-only suffix rule: if the word ends with <see cref="Suffix"/> and stripping it
/// would leave at least <see cref="MinRemainingLength"/> characters, the suffix is deleted.
/// <see cref="ExtraGuard"/> is an optional additional check (word, currentLength) for the handful
/// of rules that need more than a length guard (e.g. "don't strip a bare -s off a word ending in
/// -ss"). This is a plain data record, not a rule engine — <see cref="SuffixStripper"/> below is
/// the entire "engine", and it is a dozen lines.
/// </summary>
internal readonly record struct SuffixRule(
    string Suffix,
    int MinRemainingLength,
    Func<string, int, bool>? ExtraGuard = null);

/// <summary>
/// Shared truncation loop used by both <see cref="EnglishStemmer"/> and <see cref="RussianStemmer"/>.
/// Each language supplies its own fixed suffix table (see remarks); this class only implements the
/// mechanical "strip the longest applicable suffix, repeat until nothing applies" loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this guarantees the prefix invariant.</b> The loop only ever shrinks a length counter
/// (<c>len</c>) by removing characters from the end; the returned string is always
/// <c>word[0..len]</c>. It never inserts, replaces, or reorders a character, so the result is
/// trivially a prefix of <paramref name="word"/> (or the word itself, unchanged, if no rule ever
/// applies).
/// </para>
/// <para>
/// <b>Why this guarantees idempotency.</b> The loop runs to a fixed point: it only stops once a
/// full pass over <paramref name="rules"/> finds no suffix that both matches and satisfies its
/// guards. <c>Stem</c> is a pure function of the string content alone, so calling it again on that
/// already-reduced result re-runs the identical scan against the identical (shorter) string and
/// immediately finds the same "nothing applies" outcome on its first pass — it cannot behave
/// differently the second time. This holds regardless of how the individual suffix rules
/// interact, which is what makes double-suffix words (e.g. English "agreements" -&gt; strip "-s"
/// -&gt; "agreement" -&gt; strip "-ment" -&gt; "agree") converge correctly in one <c>Stem</c> call
/// instead of needing the caller to call it twice.
/// </para>
/// <para>
/// Performance: no substring is allocated until the very end. Matching walks backward from the
/// current length using <see cref="string.CompareOrdinal(string, int, string, int, int)"/>
/// (no allocation), so the whole loop is linear in the word length — safe for a hot path with no
/// quadratic blowup even on pathological long inputs.
/// </para>
/// </remarks>
internal static class SuffixStripper
{
    public static string StripToFixedPoint(string word, IReadOnlyList<SuffixRule> rules)
    {
        int len = word.Length;
        bool strippedAny = true;

        while (strippedAny)
        {
            strippedAny = false;

            foreach (SuffixRule rule in rules)
            {
                int suffixLength = rule.Suffix.Length;
                int remaining = len - suffixLength;
                if (remaining < rule.MinRemainingLength)
                {
                    continue;
                }

                if (string.CompareOrdinal(word, remaining, rule.Suffix, 0, suffixLength) != 0)
                {
                    continue;
                }

                if (rule.ExtraGuard is not null && !rule.ExtraGuard(word, len))
                {
                    continue;
                }

                len = remaining;
                strippedAny = true;
                break; // Re-scan from the longest suffix again against the new (shorter) length.
            }
        }

        return len == word.Length ? word : word.Substring(0, len);
    }
}

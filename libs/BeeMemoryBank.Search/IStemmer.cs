namespace BeeMemoryBank.Search;

/// <summary>
/// Reduces a normalized token to a shorter stem for search-time morphology matching.
/// </summary>
/// <remarks>
/// <para>
/// <b>The prefix invariant.</b> For every implementation in this library,
/// <c>Stem(token)</c> is guaranteed to return either an empty string or a non-empty string that
/// is a true prefix of <paramref name="normalizedToken"/> as passed in — i.e. <c>Stem(token)</c>
/// only ever deletes trailing characters, never substitutes or reorders any. This is what lets a
/// downstream full-text index run a prefix query (<c>"stem*"</c>) against the raw, unstemmed
/// indexed word forms and still match every inflected form of a word: as long as every inflected
/// form's stem is itself a prefix of that form, and all inflected forms of the same word share a
/// common stem, a prefix search for the stem matches them all without a custom tokenizer.
/// </para>
/// <para>
/// <b>Idempotency.</b> <c>Stem(Stem(x))</c> always equals <c>Stem(x)</c> — stemming an
/// already-stemmed token is a no-op, not a further truncation.
/// </para>
/// <para>
/// Implementations never throw, for any input including <c>null</c>, empty strings, whitespace,
/// single characters, digits, emoji, or mixed-script text.
/// </para>
/// </remarks>
public interface IStemmer
{
    /// <summary>
    /// Stems <paramref name="normalizedToken"/>. The caller is expected to have already run the
    /// token through <see cref="TextNormalization.Normalize"/> (or a full <see cref="ITokenizer"/>
    /// pass) — this method does not re-normalize; it only strips trailing characters.
    /// </summary>
    string Stem(string? normalizedToken);
}

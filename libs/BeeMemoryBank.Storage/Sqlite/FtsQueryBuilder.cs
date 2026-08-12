using BeeMemoryBank.Search;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// Turns a raw user search string into an FTS5 <c>MATCH</c> expression by reusing the same
/// <see cref="DefaultTokenizer"/>/<see cref="DefaultStemmer"/> pipeline the index is built around,
/// then quoting each stem as an FTS5 string literal with a trailing prefix wildcard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why prefix queries on raw, unstemmed indexed terms.</b> The FTS5 index (migration 005)
/// stores the raw word forms found in title/tree_path/name/path/tag columns — it does not store
/// stems. The <see cref="IStemmer"/> prefix invariant (see its XML doc) guarantees that
/// <c>Stem(token)</c> is always a prefix of <c>token</c>. Therefore a prefix query
/// <c>"stem"*</c> run against the raw indexed forms matches every inflected form that shares that
/// stem, with no custom SQLite tokenizer and no second stemmed copy of the text. This helper is
/// the query-side half of that contract.
/// </para>
/// <para>
/// <b>Multi-term semantics: implicit AND.</b> Stems are joined with a single space, which FTS5
/// parses as implicit AND. A search-box query like <c>"postgres runbook"</c> is expected to find
/// documents containing both words, not either; AND matches that expectation. Switching to OR is
/// a one-line change here if a future mode wants it.
/// </para>
/// <para>
/// <b>Escaping.</b> Each stem is emitted as an FTS5 string literal: wrapped in double quotes with
/// any internal double-quote doubled, per the FTS5 dialect. The trailing <c>*</c> sits outside the
/// quotes and turns the literal into a prefix token. Because the stemmer/tokenizer output is plain
/// lowercased word characters this is defense-in-depth rather than load-bearing, but it must still
/// be correct so a raw query containing FTS5-special characters (<c>"</c>, <c>*</c>, <c>AND</c>,
/// <c>(</c>, <c>:</c>, …) can never inject query syntax.
/// </para>
/// <para>
/// <b>Empty input.</b> Returns <c>null</c>; callers short-circuit to an empty result set rather
/// than sending <c>MATCH ''</c> to SQLite.
/// </para>
/// </remarks>
internal static class FtsQueryBuilder
{
    private static readonly ITokenizer Tokenizer = new DefaultTokenizer();
    private static readonly IStemmer Stemmer = new DefaultStemmer();

    /// <summary>
    /// Builds the FTS5 <c>MATCH</c> expression for <paramref name="query"/>, or <c>null</c> when
    /// the query yields no usable terms (null/empty/whitespace/punctuation-only). A <c>null</c>
    /// return is the caller's signal to return an empty result list without hitting the DB.
    /// </summary>
    public static string? BuildMatchExpression(string? query)
    {
        string? matchExpr = null;
        foreach (var token in Tokenizer.Tokenize(query))
        {
            var stem = Stemmer.Stem(token);
            if (string.IsNullOrEmpty(stem))
            {
                continue;
            }

            // FTS5 string literal: enclose in double quotes, double any internal double-quote,
            // trailing '*' (outside the quotes) makes it a prefix token.
            var term = "\"" + stem.Replace("\"", "\"\"") + "\"*";
            matchExpr = matchExpr == null ? term : matchExpr + " " + term;
        }

        return matchExpr;
    }
}

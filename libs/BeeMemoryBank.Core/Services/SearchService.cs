using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Search;
using BeeMemoryBank.Search.Indexing;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Search by metadata (title, tags) and optionally by decrypted article body.
/// Body search requires an unlocked session.
/// </summary>
public class SearchService(
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    IFolderRepository folderRepo,
    SessionService session,
    CallerScopeHolder scopeHolder,
    SearchQueryCache queryCache,
    IndexBuilder indexBuilder,
    SearchMetrics? metrics = null)
{
    // Method discriminators in the cache key: SearchAsync and SearchWithContentAsync return
    // different result sets for the same query string, so they must occupy disjoint cache slots.
    private const string MethodSearch = nameof(SearchAsync);
    private const string MethodSearchWithContent = nameof(SearchWithContentAsync);

    // WP-12: the same tokenizer/stemmer pipeline IndexBuilder uses at ingestion (see
    // IndexBuilder.TokenizeAndStem) -- a query must go through the identical pipeline so its stems
    // exactly match the stemmed dictionary IndexBuilder.SearchRanked looks up against. Stateless and
    // thread-safe, so one shared instance per pipeline stage is fine to reuse across calls.
    private static readonly ITokenizer IndexedSearchTokenizer = new DefaultTokenizer();
    private static readonly IStemmer IndexedSearchStemmer = new DefaultStemmer();

    // M11: hard cap on query length. The query string is embedded verbatim in the query-cache key,
    // fed to the FTS5 MATCH builder, and (for content search) compared against every candidate
    // body via a substring scan -- none of that is bounded by anything else, and a legitimate
    // search query has no reason to be long. Throwing here is deliberate: it's an ArgumentException,
    // which Program.cs's global exception handler already maps to 400 for every REST caller, and
    // MCP tool callers get the usual tool-error surface -- no new plumbing needed anywhere else.
    private const int MaxQueryLength = 1000;

    private static void ThrowIfQueryTooLong(string query)
    {
        if (query.Length > MaxQueryLength)
            throw new ArgumentException(
                $"Search query is too long ({query.Length} characters, max {MaxQueryLength}).");
    }

    public async Task<SearchResults> SearchAsync(string query)
    {
        ThrowIfQueryTooLong(query);

        // WP-17: every call goes through the single-flight + TTL cache. The cache key embeds the
        // caller's read-scope fingerprint so two callers with different folder ACLs can never share
        // a result (see SearchQueryCache). On a miss the underlying logic below runs unchanged.
        //
        // WP-18: wrap the call (cache included -- this is what the caller experiences) with a timing
        // measurement. The query string is NEVER passed to the metrics component; only the elapsed
        // time and the coarse result count leave this method. `metrics` is null only in direct-`new`
        // test construction (DI always injects the singleton).
        var sw = metrics is null ? null : Stopwatch.StartNew();
        var result = await queryCache.ExecuteAsync(
            MethodSearch,
            query,
            scopeHolder.Scope.ReadScopeFingerprint,
            () => SearchUncachedAsync(query));
        if (metrics is not null)
        {
            sw!.Stop();
            metrics.Record(SearchMetrics.MetadataSearch, sw.Elapsed,
                result.Folders.Count + result.Articles.Count);
        }
        return result;
    }

    private async Task<SearchResults> SearchUncachedAsync(string query)
    {
        var foldersTask = folderRepo.SearchAsync(query);
        var articlesTask = articleRepo.SearchAsync(query);
        var byIdTask = articleRepo.SearchByIdPartialAsync(query.Trim());
        await Task.WhenAll(foldersTask, articlesTask, byIdTask);

        var articles = await articlesTask;
        MergeById(articles, await byIdTask);

        return new SearchResults(await foldersTask, articles);
    }

    private static void MergeById(List<Article> into, List<Article> extra)
    {
        if (extra.Count == 0) return;
        var seen = new HashSet<Guid>(into.Select(a => a.Id));
        foreach (var a in extra)
        {
            if (seen.Add(a.Id)) into.Add(a);
        }
    }

    /// <summary>
    /// Searches article bodies by decrypting each one and checking for the query string.
    /// Requires an unlocked session. Results are merged with title/tag matches.
    /// </summary>
    public async Task<SearchResults> SearchWithContentAsync(string query)
    {
        ThrowIfQueryTooLong(query);

        // WP-17: same single-flight + TTL cache as SearchAsync. Body-content search is by far the
        // most expensive query path (it decrypts every active body), so coalescing concurrent
        // identical calls and caching near-repeat calls is where the cache pays off most.
        //
        // WP-18: timing/counting wrapper, identical contract to SearchAsync -- only elapsed time and
        // the coarse result count are recorded; the query string stays local to this method.
        var sw = metrics is null ? null : Stopwatch.StartNew();
        var result = await queryCache.ExecuteAsync(
            MethodSearchWithContent,
            query,
            scopeHolder.Scope.ReadScopeFingerprint,
            () => SearchWithContentUncachedAsync(query));
        if (metrics is not null)
        {
            sw!.Stop();
            metrics.Record(SearchMetrics.ContentSearch, sw.Elapsed,
                result.Folders.Count + result.Articles.Count);
        }
        return result;
    }

    private async Task<SearchResults> SearchWithContentUncachedAsync(string query)
    {
        var foldersTask = folderRepo.SearchAsync(query);
        var metadataTask = articleRepo.SearchAsync(query);
        var byIdTask = articleRepo.SearchByIdPartialAsync(query.Trim());
        await Task.WhenAll(foldersTask, metadataTask, byIdTask);

        var folderResults = await foldersTask;
        var metadataResults = await metadataTask;

        MergeById(metadataResults, await byIdTask);

        if (!session.IsUnlocked)
            return new SearchResults(folderResults, metadataResults);

        // M11: resolve the caller's full visible-article set BEFORE touching any encrypted body,
        // so the decrypt pass below is proportional to what THIS CALLER can actually see instead of
        // the whole vault. Previously ACL filtering only happened at the very end (GetByIdsAsync on
        // the matched ids), which meant every uncached content search -- including one from a
        // caller whose scope denies everything -- streamed and AES-decrypted every active article
        // body in the vault first and only threw the invisible results away afterwards. N distinct
        // (cache-missing) queries meant N full-vault decrypt passes, independent of what the caller
        // was ever going to be allowed to see. ListAsync() here is metadata-only (no ciphertext) and
        // already applies scopeHolder.Scope.FilterArticles, so this costs one cheap query instead of
        // decrypting every article outside the caller's scope.
        var visibleArticleIds = new HashSet<Guid>((await articleRepo.ListAsync()).Select(a => a.Id));
        if (visibleArticleIds.Count == 0)
            return new SearchResults(folderResults, metadataResults);

        var matchedIds = new HashSet<Guid>(metadataResults.Select(a => a.Id));
        var bodyMatchIds = new ConcurrentBag<Guid>();
        // One DEK snapshot is shared read-only across all worker tasks for unwrap calls.
        // It is cleared exactly once in `finally` AFTER Task.WhenAll(workers) so no worker can
        // race the clear.
        var masterDek = session.GetMasterDek();
        try
        {
            // Bounded channel: backpressure so we don't materialize the whole active-body set
            // (100k+ ciphertext blobs) in memory ahead of decryption. Single writer (the producer),
            // multiple readers (the decrypt workers).
            const int ChannelCapacity = 64;
            var channel = Channel.CreateBounded<EncryptedArticleBody>(
                new BoundedChannelOptions(ChannelCapacity) { SingleWriter = true });

            int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
            var workers = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    await foreach (var body in channel.Reader.ReadAllAsync())
                    {
                        try
                        {
                            // Skip articles already matched by the metadata (title/tag) search.
                            if (matchedIds.Contains(body.ArticleId))
                                continue;

                            var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
                            var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(body.ArticleId.ToByteArray()).ToArray() : null;
                            var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(body.ArticleId.ToByteArray()).ToArray() : null;
                            var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, dekAad);
                            var plaintext = ArticleEncryptor.Decrypt(body.Ciphertext, body.IV, articleDek, bodyAad);
                            Array.Clear(articleDek);

                            // Protected bodies are opaque BMBENC1 blobs — never full-text-search them
                            // (we have no passphrase here, and matching base64 would be meaningless).
                            if (ProtectedContentCodec.IsProtected(plaintext))
                                continue;

                            if (plaintext.Contains(query, StringComparison.OrdinalIgnoreCase))
                                bodyMatchIds.Add(body.ArticleId);
                        }
                        catch // AUDIT NOTE: Intentional — a corrupt or incompatible encrypted body
                        {    // (e.g., re-encrypted with a different DEK after key rotation) must not
                        }    // break search for all other articles. Per-item isolation is preserved:
                             // the catch lives inside the worker, so one bad body can't fault the
                             // whole parallel pipeline.
                    }
                });
            }

            // Single producer: sequential read off ONE long-lived SQLite connection (SQLite
            // connections aren't safely shared across threads). WAL holds a consistent snapshot for
            // the life of this single statement/connection, so concurrent creates/soft-deletes on
            // other connections can't shift a row out of this read the way the old LIMIT/OFFSET
            // batches over fresh connections could.
            try
            {
                await foreach (var body in bodyRepo.StreamActiveAsync())
                {
                    // M11: skip bodies the caller cannot see at all -- never even hand them to a
                    // worker for decryption, rather than filtering the match set after the fact.
                    if (!visibleArticleIds.Contains(body.ArticleId))
                        continue;
                    await channel.Writer.WriteAsync(body);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                // Surface producer failure to the workers via the channel, then rethrow after they
                // wind down so masterDek is still cleared by the finally below.
                channel.Writer.Complete(ex);
            }

            await Task.WhenAll(workers);
        }
        finally
        {
            Array.Clear(masterDek);
        }

        if (!bodyMatchIds.IsEmpty)
        {
            var bodyArticles = await articleRepo.GetByIdsAsync(bodyMatchIds.ToList());
            metadataResults.AddRange(bodyArticles);
        }

        return new SearchResults(folderResults, metadataResults);
    }

    /// <summary>
    /// WP-12: ranked full-text search over <see cref="BeeMemoryBank.Search.Indexing.IndexBuilder"/>'s
    /// in-memory inverted index -- a new, additive search capability, independent of
    /// <see cref="SearchWithContentAsync"/>'s existing linear body scan above, which this method does
    /// NOT replace, wire into, or otherwise change the behavior of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not wired into <see cref="SearchWithContentAsync"/> yet.</b>
    /// <c>PendingIndexProcessor</c> (WP-11) indexes articles into <paramref name="indexBuilder"/>'s
    /// backing <c>IndexBuilder</c> progressively in the background (<c>index_pending</c>), so at any
    /// given moment some articles may not yet be reflected in it. Silently making this the primary
    /// search path today could make search return *fewer* correct results than the existing
    /// always-complete-but-slower linear scan -- a regression a user might not notice until they go
    /// looking for something specific that just has not been indexed yet. Deciding when/how to cut
    /// over (e.g. only once a completeness signal says the index is fully caught up) is a follow-up
    /// decision for the maintainer, out of this WP's scope -- mirroring how WP-07 built FTS wiring
    /// for metadata search without touching the separate body-scan path either. This method exists so
    /// a future work package or the maintainer can wire it in once ready.
    /// </para>
    /// <para>
    /// <b>Pipeline.</b> Tokenizes+stems <paramref name="query"/> with the exact same
    /// <see cref="ITokenizer"/>/<see cref="IStemmer"/> pipeline <c>IndexBuilder</c> uses at ingestion
    /// (required -- this index stores stemmed terms and matches by exact stem, no prefix/wildcard),
    /// asks <c>IndexBuilder.SearchRanked</c> for a BM25-ranked candidate list, hydrates full
    /// <see cref="Article"/> rows for those ids, then applies <see cref="ICallerScope.FilterArticles"/>
    /// -- the same, already-audited folder-scope ACL enforcement every other read in this codebase
    /// uses, not a bespoke check reimplemented inside the index engine. Results are returned in
    /// descending-score order (re-sorted after ACL filtering, since neither <c>GetByIdsAsync</c> nor
    /// <c>FilterArticles</c> is required to preserve input order).
    /// </para>
    /// <para>
    /// <b>No snippets.</b> This WP's optional snippet extension (decrypting just the top results to
    /// show a matched-text preview) was not implemented -- a correct ranked-id-list is a complete,
    /// acceptable deliverable per the brief, and it kept the scope focused on the load-bearing
    /// ranking correctness tests. See the WP-12 report for the full reasoning.
    /// </para>
    /// </remarks>
    public async Task<List<Article>> SearchIndexedContentAsync(string query, int topK = 20)
    {
        ThrowIfQueryTooLong(query);

        List<string> stemmedTerms = TokenizeAndStemQuery(query);
        if (stemmedTerms.Count == 0)
        {
            return [];
        }

        IReadOnlyList<(Guid ArticleId, float Score)> ranked = indexBuilder.SearchRanked(stemmedTerms, topK);
        if (ranked.Count == 0)
        {
            return [];
        }

        List<Article> articles = await articleRepo.GetByIdsAsync(ranked.Select(r => r.ArticleId).ToList());
        List<Article> filtered = scopeHolder.Scope.FilterArticles(articles);

        // Preserve SearchRanked's descending-score order across the GetByIdsAsync/FilterArticles
        // round-trip, neither of which is documented to preserve input order.
        Dictionary<Guid, int> rankByArticleId = ranked
            .Select((result, index) => (result.ArticleId, index))
            .ToDictionary(x => x.ArticleId, x => x.index);
        filtered.Sort((a, b) => rankByArticleId[a.Id].CompareTo(rankByArticleId[b.Id]));

        return filtered;
    }

    private static List<string> TokenizeAndStemQuery(string? query)
    {
        var terms = new List<string>();
        foreach (string token in IndexedSearchTokenizer.Tokenize(query))
        {
            string stem = IndexedSearchStemmer.Stem(token);
            if (stem.Length > 0)
            {
                terms.Add(stem);
            }
        }

        return terms;
    }
}

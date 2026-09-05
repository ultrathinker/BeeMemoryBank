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

    // Same reasoning as the two constants above, extended to the index-first web content-search
    // path added alongside them: SearchWebContentAsync's result set (ranked-index matches, plus a
    // pending-only linear-scan top-up) is not guaranteed to match SearchWithContentAsync's (a full
    // linear scan) article-for-article ordering, even though both search the same corpus, so they
    // must not share a cache slot either.
    private const string MethodSearchWebContent = nameof(SearchWebContentAsync);

    /// <summary>
    /// How many index_pending articles the web content search will scan individually before giving
    /// up on the restricted fallback and running the ordinary linear scan instead.
    ///
    /// <para>Two failure modes sit on either side of this number. Below it, scanning the backlog
    /// one article at a time is far cheaper than decrypting the vault. Above it — the state every
    /// article is in immediately after a full index rebuild — the "restricted" scan IS the
    /// full-vault scan, only without the bounded-channel pipeline that path has for streaming that
    /// much ciphertext, and with an `IN` list long enough to exceed SQLite's parameter limit.</para>
    ///
    /// <para>2000 is chosen as roughly the largest backlog a five-minute processor interval
    /// produces under normal write load, so ordinary editing stays on the cheap path and only a
    /// genuine rebuild crosses over.</para>
    /// </summary>
    private const int MaxRestrictedFallbackIds = 2000;

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
                        // Skip articles already matched by the metadata (title/tag) search.
                        if (matchedIds.Contains(body.ArticleId))
                            continue;

                        if (BodyMatchesQuery(body, masterDek, query))
                            bodyMatchIds.Add(body.ArticleId);
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
    /// Decrypts one article body and reports whether its plaintext contains <paramref name="query"/>
    /// (case-insensitively). Factored out of the worker loop above so <see cref="SearchWebContentAsync"/>'s
    /// pending-only fallback scan below can share the exact same DEK-unwrap/AAD/protected-content
    /// logic instead of drifting out of sync with a second copy of it.
    /// </summary>
    /// <param name="masterDek">
    /// Caller-owned DEK snapshot. Read-only here (never mutated or cleared) -- clearing it once
    /// every decrypt attempt across a batch has finished is the caller's responsibility, exactly as
    /// it was before this was extracted into its own method.
    /// </param>
    private static bool BodyMatchesQuery(EncryptedArticleBody body, byte[] masterDek, string query)
    {
        try
        {
            var isV1 = body.EncryptedDek.Length > 48 && body.EncryptedDek[0] == 0x01;
            var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(body.ArticleId.ToByteArray()).ToArray() : null;
            var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(body.ArticleId.ToByteArray()).ToArray() : null;
            var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek, dekAad);
            var plaintext = ArticleEncryptor.Decrypt(body.Ciphertext, body.IV, articleDek, bodyAad);
            Array.Clear(articleDek);

            // Protected bodies are opaque BMBENC1 blobs — never full-text-search them
            // (we have no passphrase here, and matching base64 would be meaningless).
            if (ProtectedContentCodec.IsProtected(plaintext))
                return false;

            return plaintext.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        catch // AUDIT NOTE: Intentional — a corrupt or incompatible encrypted body (e.g.,
        {     // re-encrypted with a different DEK after key rotation) must not break search for
              // all other articles. Every call site isolates this per-item, so one bad body can
              // never fault a whole parallel batch.
            return false;
        }
    }

    /// <summary>
    /// WP-12: ranked full-text search over <see cref="BeeMemoryBank.Search.Indexing.IndexBuilder"/>'s
    /// in-memory inverted index. Originally an additive, standalone capability independent of
    /// <see cref="SearchWithContentAsync"/>'s linear body scan; now also the primary source
    /// <see cref="SearchWebContentAsync"/> composes into the web content-search path (see that
    /// method's own remarks for how completeness is verified before trusting the index alone).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this was not wired into the web content-search path when it was first built.</b>
    /// <c>PendingIndexProcessor</c> (WP-11) indexes articles into <paramref name="indexBuilder"/>'s
    /// backing <c>IndexBuilder</c> progressively in the background (<c>index_pending</c>), so at any
    /// given moment some articles may not yet be reflected in it. Silently making this the primary
    /// search path without a completeness check could make search return *fewer* correct results
    /// than the existing always-complete-but-slower linear scan -- a regression a user might not
    /// notice until they go looking for something specific that just has not been indexed yet.
    /// <see cref="SearchWebContentAsync"/> is what closes that gap: it consults
    /// <c>IArticleRepository.GetIndexPendingIdsUnscopedAsync</c> (backed by the indexed
    /// <c>index_pending</c> column) and only trusts this method's results alone once that backlog is
    /// empty, falling back to a linear scan restricted to just the still-pending ids otherwise. This
    /// method itself is unchanged by that -- it still has no opinion on completeness and simply
    /// returns whatever the index currently knows, which is exactly why it remains directly usable
    /// on its own too (e.g. by <c>HybridSearchService</c>'s MCP-facing keyword mode, where a
    /// momentarily-incomplete index has always been an accepted tradeoff for tool-call latency).
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

    /// <summary>
    /// The web content-search path (<c>content=true</c> on <c>GET /api/search</c>): serves body
    /// matches from <see cref="SearchIndexedContentAsync"/>'s ranked BM25 index instead of
    /// <see cref="SearchWithContentAsync"/>'s full linear decrypt-every-body scan, falling back to a
    /// linear scan ONLY for the (normally empty, or small) set of articles the background indexer
    /// has not caught up on yet -- see <see cref="SearchWebContentUncachedAsync"/> for exactly how
    /// that fallback is scoped. <see cref="SearchWithContentAsync"/> itself is untouched by this and
    /// remains available as the always-complete-but-slower fallback for any other caller that still
    /// wants it.
    /// </summary>
    /// <remarks>
    /// Same cache/metrics wrapper shape as <see cref="SearchAsync"/>/<see cref="SearchWithContentAsync"/>
    /// above -- single-flight + TTL cache under its own <see cref="MethodSearchWebContent"/> slot, and
    /// the same elapsed-time/coarse-result-count-only metrics recording (still under the shared
    /// <see cref="SearchMetrics.ContentSearch"/> label: from the admin dashboard's point of view this
    /// is still "content search", just computed differently underneath).
    /// </remarks>
    public async Task<SearchResults> SearchWebContentAsync(string query)
    {
        ThrowIfQueryTooLong(query);

        var sw = metrics is null ? null : Stopwatch.StartNew();
        var result = await queryCache.ExecuteAsync(
            MethodSearchWebContent,
            query,
            scopeHolder.Scope.ReadScopeFingerprint,
            () => SearchWebContentUncachedAsync(query));
        if (metrics is not null)
        {
            sw!.Stop();
            metrics.Record(SearchMetrics.ContentSearch, sw.Elapsed,
                result.Folders.Count + result.Articles.Count);
        }
        return result;
    }

    private async Task<SearchResults> SearchWebContentUncachedAsync(string query)
    {
        // Metadata (title/tag) matching is identical to SearchWithContentAsync's — unaffected by
        // where content matches come from below.
        var foldersTask = folderRepo.SearchAsync(query);
        var metadataTask = articleRepo.SearchAsync(query);
        var byIdTask = articleRepo.SearchByIdPartialAsync(query.Trim());
        await Task.WhenAll(foldersTask, metadataTask, byIdTask);

        var folderResults = await foldersTask;
        var metadataResults = await metadataTask;
        MergeById(metadataResults, await byIdTask);

        // Same locked-session contract as SearchWithContentAsync: no body-derived results at all
        // while locked, metadata-only. The ranked index itself needs no live decryption to query (it
        // holds term postings, never ciphertext or plaintext), but the invariant this preserves is a
        // product one, not a technical one -- a locked session must not surface ANY result that was
        // ever derived from a body's plaintext, full stop, so the same guard applies here too.
        if (!session.IsUnlocked)
            return new SearchResults(folderResults, metadataResults);

        var matchedIds = new HashSet<Guid>(metadataResults.Select(a => a.Id));

        // Primary source: the ranked BM25 index. topK: int.MaxValue rather than the 20-result
        // default other SearchIndexedContentAsync callers use -- SearchRanked already bounds its
        // own candidate set to however many documents satisfy the implicit-AND query (see its own
        // doc comment), so there is no separate "top N" cap layered on top here, matching
        // SearchWithContentAsync's own uncapped "every matching article" contract exactly.
        List<Article> rankedMatches = await SearchIndexedContentAsync(query, int.MaxValue);
        var contentMatches = new List<Article>(rankedMatches.Count);
        foreach (Article a in rankedMatches)
        {
            if (matchedIds.Add(a.Id))
                contentMatches.Add(a);
        }

        // The completeness check: index_pending flags exactly which active articles
        // PendingIndexProcessor has not yet folded into the index. Zero means the ranked results
        // above are already the complete answer and nothing else runs.
        //
        // Do NOT assume zero is the common case. PendingIndexProcessor wakes on a five-minute
        // timer, so on a vault with twenty people writing, something is almost always within five
        // minutes of its last save and the backlog is rarely empty during working hours. That is
        // why the fallback below has to stay genuinely cheap rather than merely rare: it is on the
        // hot path most of the day, not an edge case.
        //
        // The cap is the other half. After TriggerFullRebuildAsync every article is pending at
        // once, and a "restricted" scan over all of them is the full-vault scan this method exists
        // to avoid — with the added failure of a Dapper `IN` list past SQLite's parameter limit.
        // Past the cap the honest answer is the linear path, which has a bounded-channel pipeline
        // built for exactly that size of job. Asking for one more than the cap is what makes
        // "there are more than this" answerable without reading them.
        List<Guid> pendingIds = await articleRepo.GetIndexPendingIdsUnscopedAsync(MaxRestrictedFallbackIds + 1);

        if (pendingIds.Count > MaxRestrictedFallbackIds)
            return await SearchWithContentUncachedAsync(query);

        if (pendingIds.Count > 0)
        {
            List<Article> pendingMatches = await ScanPendingArticlesAsync(query, pendingIds, matchedIds);
            contentMatches.AddRange(pendingMatches);
        }

        metadataResults.AddRange(contentMatches);
        return new SearchResults(folderResults, metadataResults);
    }

    /// <summary>
    /// The "restricted to just the pending article ids" fallback: decrypts and substring-matches
    /// ONLY the articles <paramref name="pendingIds"/> names (further narrowed to what the caller
    /// can actually see, and to what the ranked-index pass hasn't already matched), never the whole
    /// active-body set the way <see cref="SearchWithContentUncachedAsync"/> does. This is the
    /// difference that keeps a query cheap while a handful of rows are still index_pending, instead
    /// of silently degrading back to a full-vault scan the moment even one row is behind.
    /// </summary>
    /// <param name="pendingIds">
    /// The GLOBAL, unscoped index_pending backlog from <c>GetIndexPendingIdsUnscopedAsync</c> -- may
    /// include articles this caller cannot see at all, which is why this method still resolves its
    /// own caller-visible id set below before touching any ciphertext (same M11 principle
    /// <see cref="SearchWithContentUncachedAsync"/> already applies to its own, much larger, scan).
    /// </param>
    private async Task<List<Article>> ScanPendingArticlesAsync(
        string query, List<Guid> pendingIds, HashSet<Guid> matchedIds)
    {
        // Visibility is resolved from the BACKLOG, not from the vault. The obvious way to write
        // this is ListAsync() into a HashSet and test membership — but ListAsync() reads every
        // visible article row, so on a 100k vault that reintroduces an O(vault) step on the very
        // path built to remove one, and (see the caller) that path runs most of the working day
        // rather than rarely.
        //
        // GetByIdsAsync answers the same question over the pending ids alone, and it applies the
        // caller's folder scope itself (ArticleRepository.GetByIdsAsync ends in
        // FilterArticles), so what comes back IS the visible subset — no second filter needed and
        // none implied.
        var candidateIds = pendingIds.Where(id => !matchedIds.Contains(id)).ToList();
        if (candidateIds.Count == 0)
            return [];

        var visible = await articleRepo.GetByIdsAsync(candidateIds);
        if (visible.Count == 0)
            return [];

        var idsToScan = visible.Select(a => a.Id).ToList();

        // The SQL-level id filter (not a stream-then-skip) is what actually restricts the blob reads
        // -- see GetByArticleIdsAsync's own doc comment for why that distinction matters here.
        List<EncryptedArticleBody> bodies = await bodyRepo.GetByArticleIdsAsync(idsToScan);
        if (bodies.Count == 0)
            return [];

        // One DEK snapshot shared read-only across the parallel decrypt pass below, cleared exactly
        // once after every task has finished with it -- same pattern (and the same reason) as
        // SearchWithContentUncachedAsync's own masterDek handling.
        var masterDek = session.GetMasterDek();
        var matchedPendingIds = new ConcurrentBag<Guid>();
        try
        {
            // A plain Parallel.ForEachAsync over the already-materialized (and, by construction,
            // backlog-sized rather than vault-sized) list is deliberately simpler than
            // SearchWithContentUncachedAsync's bounded-channel producer/consumer pipeline: that
            // pipeline's whole purpose is to avoid materializing an UNKNOWN, potentially vault-sized
            // sequence in memory ahead of decryption, which does not apply here -- `bodies` is
            // already a small, known-size, already-in-memory list by the time this runs.
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };
            await Parallel.ForEachAsync(bodies, options, (body, _) =>
            {
                if (BodyMatchesQuery(body, masterDek, query))
                    matchedPendingIds.Add(body.ArticleId);
                return ValueTask.CompletedTask;
            });
        }
        finally
        {
            Array.Clear(masterDek);
        }

        if (matchedPendingIds.IsEmpty)
            return [];

        // The rows are already in hand from the visibility resolution above and already scoped,
        // so the matched subset is selected rather than re-queried. Re-hydrating here would be a
        // second scope-filtered round trip for rows this method loaded a moment ago.
        var matched = matchedPendingIds.ToHashSet();
        return visible.Where(a => matched.Contains(a.Id)).ToList();
    }

    private static List<string> TokenizeAndStemQuery(string? query)
    {
        var terms = new List<string>();
        foreach (string token in IndexedSearchTokenizer.Tokenize(query))
        {
            // Query-time stop-word removal (a deliberate, standard behavior change; see StopWords).
            // Matched on the SURFACE token -- the tokenizer already normalized it, and this is BEFORE
            // stemming, which is where a natural-form stop-word list ("the", "и") lines up exactly.
            // A near-ubiquitous term like "the"/"и" carries almost no ranking signal yet forces a
            // full-postings walk in the index, so it is dropped from the query entirely. If the whole
            // query is stop words the result list is empty (every caller short-circuits on Count == 0
            // -- matching the entire corpus would be useless), which is why nothing is re-indexed.
            if (StopWords.IsStopWord(token))
            {
                continue;
            }

            string stem = IndexedSearchStemmer.Stem(token);
            if (stem.Length > 0)
            {
                terms.Add(stem);
            }
        }

        return terms;
    }
}

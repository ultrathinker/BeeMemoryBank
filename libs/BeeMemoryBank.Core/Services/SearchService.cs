using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
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
    SearchMetrics? metrics = null)
{
    // Method discriminators in the cache key: SearchAsync and SearchWithContentAsync return
    // different result sets for the same query string, so they must occupy disjoint cache slots.
    private const string MethodSearch = nameof(SearchAsync);
    private const string MethodSearchWithContent = nameof(SearchWithContentAsync);

    public async Task<SearchResults> SearchAsync(string query)
    {
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
                    await channel.Writer.WriteAsync(body);
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
}

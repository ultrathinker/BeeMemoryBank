using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Regression tests for the lost-article bug in body-content search (WP-02).
///
/// The old <see cref="SearchService.SearchWithContentAsync"/> paginated the active-body set with
/// repeated <c>LIMIT/OFFSET</c> queries, each on a FRESH SQLite connection. Under concurrent
/// creates/soft-deletes the <c>ORDER BY article_id</c> window shifts between batches and an
/// article can silently fall into a gap — never returned by any batch, with no error. These
/// tests exercise exactly that race: a body-content search runs concurrently with a background
/// writer that churns the active set, and assert every "needle" article is still found.
///
/// The fix streams the whole active-body set over a single, long-lived connection (one WAL
/// snapshot for the whole read) and fans decryption out across worker tasks via a bounded
/// channel. Under the fix the snapshot is consistent, so no row can be lost regardless of
/// concurrent writes.
/// </summary>
public class SearchContentConcurrencyTests : TestFixture
{
    private const string Needle = "ZQX_NEEDLE_REGRESSION_wp02_ZQX";
    private const string FillerBody =
        "Filler content for the concurrency regression test. Just padding so each article has a real body. ";

    // Total corpus size. Large enough that the old batched code (batch size 50) crossed several
    // batch boundaries, giving the race many windows to drop a row through.
    private const int TotalArticles = 250;

    // Repeat the whole search-while-churning scenario this many times: race bugs are flaky by
    // construction, so a single run is not a meaningful guard (the brief calls for repetition).
    private const int Iterations = 20;
    private const int WriterCount = 3;
    private const int OldPageSize = 50;
    private static readonly TimeSpan WriterReadyDeadline = TimeSpan.FromSeconds(30);

    // Ranks (0-indexed, in the SQLite ORDER BY article_id order) at which to place needles.
    // Straddling the OLD batch boundaries (50, 100, 150, 200) is where a single rank shift drops
    // a row through the gap; a few interior ranks add coverage.
    private static readonly int[] NeedleRanks =
        { 49, 50, 51, 99, 100, 101, 149, 150, 151, 199, 200, 201, 30, 120, 175, 230 };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    [Fact]
    public async Task SearchWithContent_FindsAllNeedles_UnderConcurrentMutation()
    {
        // ── 1. Seed the corpus with plain filler bodies (no needle substring yet). ──
        for (int i = 0; i < TotalArticles; i++)
        {
            await ArticleService.CreateAsync(
                $"Filler {i}", "/", [], FillerBody + i);
        }

        // ── 2. Designate needles at boundary-prone ranks. ──
        // Compute the exact rank order the way the repository does (ORDER BY article_id) by asking
        // SQLite directly — don't try to replicate its TEXT-vs-BLOB Guid collation in .NET.
        var orderedIds = await GetActiveIdsOrderedAsync();
        orderedIds.Count.Should().Be(TotalArticles);

        var needleIds = new HashSet<Guid>();
        foreach (var rank in NeedleRanks)
        {
            if (rank >= 0 && rank < orderedIds.Count)
                needleIds.Add(orderedIds[rank]);
        }
        needleIds.Should().NotBeEmpty("must have designated at least one needle");

        // Rewrite each needle's body to embed the distinctive substring. Title/tags stay filler so
        // the needle is reachable ONLY via body-content search (the buggy path), not via the
        // metadata search that SearchWithContentAsync also runs.
        foreach (var id in needleIds)
            await ArticleService.UpdateAsync(id, plaintext: FillerBody + " " + Needle);

        // Keep one non-needle row before the rank-50 needle reserved for the deterministic page
        // boundary mutation below. Deleting it after the first old 50-row page is read makes the
        // old OFFSET 50 page skip the needle at rank 50.
        var boundaryVictimId = orderedIds[0];
        var boundaryNeedleId = orderedIds[OldPageSize];
        needleIds.Should().Contain(boundaryNeedleId);

        // Non-needle ids are the churn's pool of deletable rows (rank shifts). Read-only snapshot.
        var deletablePool = orderedIds
            .Where(id => !needleIds.Contains(id) && id != boundaryVictimId)
            .ToArray();

        // ── 3. Repeat the search-under-mutation scenario. ──
        for (int iter = 0; iter < Iterations; iter++)
        {
            using var stopChurn = new CancellationTokenSource();
            var deletedThisIteration = new ConcurrentQueue<Guid>();
            var writersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pageBoundaryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var boundaryMutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writerErrors = new ConcurrentQueue<Exception>();
            int readyWriterCount = 0;
            int nextDeleteIndex = 0;

            void RecordWriterFailure(int writerNumber, Exception exception)
            {
                var failure = new InvalidOperationException(
                    $"Search churn writer {writerNumber} failed unexpectedly.", exception);
                writerErrors.Enqueue(failure);
                writersReady.TrySetException(failure);
                pageBoundaryReached.TrySetException(failure);
                boundaryMutationCompleted.TrySetException(failure);
            }

            var bodyRepo = new PageBoundaryBodyRepository(
                new ArticleBodyRepository(Factory),
                async () =>
                {
                    pageBoundaryReached.TrySetResult();
                    await boundaryMutationCompleted.Task.WaitAsync(
                        WriterReadyDeadline, stopChurn.Token);
                });
            var searchService = new SearchService(
                ArticleRepo,
                bodyRepo,
                FolderRepo,
                Session,
                ScopeHolder,
                new SearchQueryCache(),
                IndexBuilder);

            // Each writer first completes a setup create/delete on its own connection. The
            // reserved delete below then runs between the first two old pages; further churn is
            // released only after this search has completed so it cannot blur that boundary.
            var churnTasks = Enumerable.Range(0, WriterCount).Select(writerNumber => Task.Run(() =>
                ChurnAsync(
                    writerNumber,
                    deletablePool,
                    deletedThisIteration,
                    boundaryVictimId,
                    pageBoundaryReached,
                    boundaryMutationCompleted,
                    searchStarted,
                    searchCompleted,
                    stopChurn.Token,
                    () =>
                    {
                        if (Interlocked.Increment(ref readyWriterCount) == WriterCount)
                            writersReady.TrySetResult();
                    },
                    RecordWriterFailure,
                    () => Interlocked.Increment(ref nextDeleteIndex)))).ToArray();

            SearchResults? results = null;
            Exception? testFailure = null;
            try
            {
                try
                {
                    await writersReady.Task.WaitAsync(WriterReadyDeadline);
                }
                catch (TimeoutException)
                {
                    testFailure = new Xunit.Sdk.XunitException(
                        $"Search churn did not observe one successful mutation from each " +
                        $"{WriterCount} writers within {WriterReadyDeadline.TotalSeconds:0} seconds " +
                        $"({readyWriterCount}/{WriterCount} writers ready).");
                }
                catch (Exception exception)
                {
                    testFailure = exception;
                }

                if (testFailure is null)
                {
                    searchStarted.TrySetResult();
                    results = await searchService.SearchWithContentAsync(Needle);
                    searchCompleted.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                testFailure = exception;
            }
            finally
            {
                stopChurn.Cancel();
                searchStarted.TrySetCanceled();
                searchCompleted.TrySetCanceled();
                pageBoundaryReached.TrySetCanceled();
                boundaryMutationCompleted.TrySetCanceled();
                try
                {
                    await Task.WhenAll(churnTasks);
                }
                catch (Exception exception)
                {
                    writerErrors.Enqueue(new InvalidOperationException(
                        "A search churn writer task failed unexpectedly.", exception));
                }
            }

            await ReactivateDeletedArticlesAsync(deletedThisIteration);

            // Every writer exception is reported after all writers have stopped. The cleanup
            // above must complete first so a failure cannot leave a writer using this fixture
            // while the next assertion or iteration starts.
            if (!writerErrors.IsEmpty)
                throw new AggregateException("Concurrent search writer failure.", writerErrors);
            if (testFailure is not null)
                ExceptionDispatchInfo.Capture(testFailure).Throw();

            // ── Assertion: every needle must be present, every iteration. ──
            var foundIds = results!.Articles.Select(a => a.Id).ToHashSet();
            var missing = needleIds.Except(foundIds).ToList();
            missing.Should().BeEmpty(
                "iteration {0}: body-content search dropped {1} needle(s) under concurrent mutation " +
                "(the lost-article race). Found {2} of {3} needles.",
                iter, missing.Count, needleIds.Count - missing.Count, needleIds.Count);
        }
    }

    [Fact]
    public async Task SearchWithContent_LockedSession_DegradesToMetadataOnly()
    {
        // Behavioral invariant preserved by the fix: a locked session must not attempt body
        // decryption and must simply return metadata results (no throw).
        await ArticleService.CreateAsync("Plain", "/", [], "body " + Needle);
        Session.Lock();

        var act = async () => await SearchService.SearchWithContentAsync(Needle);
        var results = await act();
        results.Articles.Should().BeEmpty("locked session must not search bodies");
    }

    private async Task ChurnAsync(
        int writerNumber,
        Guid[] pool,
        ConcurrentQueue<Guid> deleted,
        Guid boundaryVictimId,
        TaskCompletionSource pageBoundaryReached,
        TaskCompletionSource boundaryMutationCompleted,
        TaskCompletionSource searchStarted,
        TaskCompletionSource searchCompleted,
        CancellationToken ct,
        Action writerReady,
        Action<int, Exception> recordWriterFailure,
        Func<int> nextDeleteIndex)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var setupArticle = await ArticleService.CreateAsync(
                $"Search churn setup {writerNumber}", "/", [], FillerBody + " setup");
            await ArticleService.DeleteAsync(setupArticle.Id);
            writerReady();

            await searchStarted.Task.WaitAsync(ct);

            if (writerNumber == 0)
            {
                await pageBoundaryReached.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                await ArticleService.DeleteAsync(boundaryVictimId);
                deleted.Enqueue(boundaryVictimId);
                boundaryMutationCompleted.TrySetResult();
            }
            else
            {
                await boundaryMutationCompleted.Task.WaitAsync(ct);
            }

            await searchCompleted.Task.WaitAsync(ct);

            var rng = new Random();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (rng.Next(2) == 0 && pool.Length > 0)
                {
                    // (a) delete a pre-existing non-needle filler → rank shift.
                    var victim = pool[nextDeleteIndex() % pool.Length];
                    await ArticleService.DeleteAsync(victim);
                    deleted.Enqueue(victim);
                }
                else
                {
                    // (b) create an ephemeral filler, then delete it.
                    var created = await ArticleService.CreateAsync(
                        "Ephemeral", "/", [], FillerBody + " ephemeral");
                    await ArticleService.DeleteAsync(created.Id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is the only expected exit; all other writer failures are reported.
        }
        catch (Exception exception)
        {
            recordWriterFailure(writerNumber, exception);
        }
    }

    private async Task ReactivateDeletedArticlesAsync(ConcurrentQueue<Guid> deleted)
    {
        var ids = deleted.Distinct().ToArray();
        if (ids.Length == 0)
            return;

        using var conn = (DbConnection)Factory.CreateConnection();
        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tbl_article SET status = 'A', deleted_at = NULL WHERE id = @id";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = id;
            cmd.Parameters.Add(parameter);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class PageBoundaryBodyRepository(
        IArticleBodyRepository inner,
        Func<Task> afterFirstPage) : IArticleBodyRepository
    {
        private int _rowsRead;
        private int _callbackStarted;

        public Task<EncryptedArticleBody?> GetByArticleIdAsync(Guid articleId) =>
            inner.GetByArticleIdAsync(articleId);

        public Task<List<EncryptedArticleBody>> GetAllActiveAsync() => inner.GetAllActiveAsync();

        public Task<List<EncryptedArticleBody>> GetByArticleIdsAsync(IReadOnlyCollection<Guid> articleIds) =>
            inner.GetByArticleIdsAsync(articleIds);

        public Task UpsertAsync(EncryptedArticleBody body, IDbTransaction? transaction = null) =>
            inner.UpsertAsync(body, transaction);

        public async IAsyncEnumerable<EncryptedArticleBody> StreamActiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var body in inner.StreamActiveAsync(cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return body;
                if (Interlocked.Increment(ref _rowsRead) == OldPageSize &&
                    Interlocked.Exchange(ref _callbackStarted, 1) == 0)
                {
                    await afterFirstPage().ConfigureAwait(false);
                }
            }
        }

        public Task<int> PurgeForDeletedArticlesOlderThanAsync(DateTime cutoff) =>
            inner.PurgeForDeletedArticlesOlderThanAsync(cutoff);
    }

    /// <summary>
    /// Returns active article ids in the exact order the buggy batched query used
    /// (<c>ORDER BY article_id</c>), by querying SQLite directly. This is the rank the old
    /// <c>LIMIT/OFFSET</c> windowing operated on.
    /// </summary>
    private async Task<List<Guid>> GetActiveIdsOrderedAsync()
    {
        using var conn = (DbConnection)Factory.CreateConnection();
        await using (conn.ConfigureAwait(false))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT b.article_id
                                FROM tbl_article_body b
                                JOIN tbl_article a ON a.id = b.article_id
                                WHERE a.status = 'A'
                                ORDER BY b.article_id";
            await using var reader = await cmd.ExecuteReaderAsync();
            var ids = new List<Guid>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetGuid(0));
            return ids;
        }
    }
}

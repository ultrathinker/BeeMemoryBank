using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;

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
        var allIds = new List<Guid>(TotalArticles);
        for (int i = 0; i < TotalArticles; i++)
        {
            var article = await ArticleService.CreateAsync(
                $"Filler {i}", "/", [], FillerBody + i);
            allIds.Add(article.Id);
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

        // Non-needle ids are the churn's pool of deletable rows (rank shifts). Read-only snapshot.
        var deletablePool = allIds.Where(id => !needleIds.Contains(id)).ToArray();

        // ── 3. Repeat the search-under-mutation scenario. ──
        for (int iter = 0; iter < Iterations; iter++)
        {
            var stopChurn = new CancellationTokenSource();
            var deletedThisIteration = new ConcurrentQueue<Guid>();

            // Background writers: continuously churn the active set so the old LIMIT/OFFSET
            // windowing would shift between batches. Two kinds of mutation, both on connections
            // separate from the search's connection (the exact multi-connection scenario):
            //   (a) delete a pre-existing non-needle filler → shifts later rows' ranks DOWN,
            //       which is the clean trigger for a boundary needle falling through a gap;
            //   (b) create an ephemeral filler then immediately delete it → adds a transient row.
            var churnTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                ChurnAsync(deletablePool, deletedThisIteration, stopChurn.Token))).ToArray();

            // Give the writers a beat to start mutating before the search kicks off.
            await Task.Delay(5);

            var results = await SearchService.SearchWithContentAsync(Needle);

            stopChurn.Cancel();
            try { await Task.WhenAll(churnTasks); }
            catch (OperationCanceledException) { /* expected — we cancelled */ }

            // Restore the non-needle population so the next iteration has the same density
            // (keeps needle ranks roughly stable across iterations).
            foreach (var _ in deletedThisIteration)
                await ArticleService.CreateAsync("Re-seed filler", "/", [], FillerBody);

            // ── Assertion: every needle must be present, every iteration. ──
            var foundIds = results.Articles.Select(a => a.Id).ToHashSet();
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

    private async Task ChurnAsync(Guid[] pool, ConcurrentQueue<Guid> deleted, CancellationToken ct)
    {
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (rng.Next(2) == 0 && pool.Length > 0)
                {
                    // (a) delete a pre-existing non-needle filler → rank shift.
                    var victim = pool[rng.Next(pool.Length)];
                    await ArticleService.DeleteAsync(victim).WaitAsync(ct);
                    deleted.Enqueue(victim);
                }
                else
                {
                    // (b) create an ephemeral filler, then delete it.
                    var created = await ArticleService
                        .CreateAsync("Ephemeral", "/", [], FillerBody + " ephemeral").WaitAsync(ct);
                    await ArticleService.DeleteAsync(created.Id).WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Transient SQLite busy/locked under contention is tolerated — the churn is
                // best-effort pressure; only the search results are asserted.
            }
        }
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

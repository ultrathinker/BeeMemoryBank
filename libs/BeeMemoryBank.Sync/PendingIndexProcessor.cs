using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Sync.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Background service for keeping the in-memory search index (<see cref="IndexBuilder"/>, via
/// <see cref="SearchIndexLifecycleService"/>) up to date with article content. Mirrors
/// <see cref="PendingEmbeddingProcessor"/>'s shape exactly: requires an unlocked session (skips the
/// cycle if locked), pulls a batch of index_pending = 1 articles, decrypts each body via
/// <see cref="ArticleService"/>, calls <see cref="IndexBuilder.AddOrUpdateDocument"/>, and clears
/// index_pending once done.
///
/// <para>
/// Unlike embeddings, this WP's ingestion also has two extra jobs on every cycle: (1) run the
/// unlock warm-start once (see <see cref="SearchIndexLifecycleService.EnsureWarmStartedAsync"/>,
/// invoked unconditionally every cycle the same way
/// <c>EmbeddingProjectionService.EnsureProjectionMatrixAsync</c> is -- both are idempotent no-ops
/// after the first successful run), and (2) persist a newly-sealed segment and any tombstones
/// IndexBuilder reports as a side effect of ingesting this cycle's batch.
/// </para>
///
/// <para>
/// Protected articles are skipped (never indexed): <see cref="IndexBuilder"/> has no opinion on
/// protection by design (its own doc comment says so explicitly) -- this processor is the caller
/// responsible for that filter, since a protected article's body is an opaque
/// passphrase-encrypted blob that would only ever contribute ciphertext noise terms to the index.
/// </para>
/// </summary>
public class PendingIndexProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<PendingIndexProcessor> logger,
    TimeSpan? interval = null,
    int? batchSize = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(5);
    private readonly int _batchSize = batchSize ?? 50;

    // Guards against the periodic tick and a manual DrainAllPendingAsync (or two concurrent
    // manual drains) running ProcessPendingCoreAsync at the same time. AGY review 2026-08-12:
    // two concurrent runs pull the same pending batch, both seal+persist the same hot-buffer
    // contents as separate segments with distinct GUIDs, and the next merge finds the same
    // article live in two sealed segments and throws (tombstoning invariant violation) --
    // permanently wedging the indexer until the duplicate manifest rows are pruned by hand.
    private readonly SemaphoreSlim _runLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error processing pending search index entries");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one batch. Returns the number of pending items resolved, 0 if there was nothing to
    /// do, or -1 if another run (the periodic tick or a concurrent drain) is already in flight --
    /// callers that care about the difference (see <see cref="DrainAllPendingAsync"/>) should
    /// back off and retry rather than treating -1 the same as "nothing pending".
    /// </summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return -1;
        try
        {
            return await ProcessPendingCoreAsync(ct);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<int> ProcessPendingCoreAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();

        // The search index is built from decrypted article bodies -- same precondition as
        // embeddings' projection-matrix step. Skip the whole cycle (including warm-start, which
        // itself needs the master DEK to decrypt segments) while locked, same as
        // PendingEmbeddingProcessor does.
        if (!session.IsUnlocked) return 0;

        var lifecycle = scope.ServiceProvider.GetRequiredService<SearchIndexLifecycleService>();
        await lifecycle.EnsureWarmStartedAsync(ct);

        var articleRepo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
        var pending = await articleRepo.GetIndexPendingAsync(_batchSize);
        if (pending.Count == 0) return 0;

        logger.LogInformation("Processing {Count} articles with pending search index updates", pending.Count);

        int processed = 0;
        int resolved = 0;
        foreach (Article article in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (!session.IsUnlocked) break;
            try
            {
                if (article.Protected)
                {
                    // Never index a protected article's body -- it's an opaque
                    // passphrase-encrypted blob. Clear the pending flag so the background
                    // processor stops retrying it; it simply won't appear in search (by design),
                    // mirroring EmbeddingProjectionService.ProjectArticleAsync's identical
                    // protected-article skip.
                    await articleRepo.ClearIndexPendingUnscopedAsync(article.Id);
                    resolved++;
                    continue;
                }

                var articleService = scope.ServiceProvider.GetRequiredService<ArticleService>();
                var plaintext = await articleService.GetContentAsync(article.Id);

                int sealCountBefore = lifecycle.Builder.SealCount;
                IReadOnlyList<SegmentTombstoneEvent> tombstoneEvents =
                    lifecycle.Builder.AddOrUpdateDocument(article.Id, article.FolderId ?? Guid.Empty, plaintext);

                if (tombstoneEvents.Count > 0)
                {
                    await lifecycle.PersistTombstonesAsync(tombstoneEvents, ct);
                }

                if (lifecycle.Builder.SealCount > sealCountBefore)
                {
                    await lifecycle.PersistMostRecentlySealedSegmentAsync(ct);
                }

                await articleRepo.ClearIndexPendingUnscopedAsync(article.Id);
                processed++;
                resolved++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to index article {ArticleId}", article.Id);
            }
        }

        if (processed > 0)
            logger.LogInformation("Indexed articles: {Count}", processed);

        // Progress is "pending items resolved", not just "actually indexed" -- a batch made up
        // entirely of protected articles clears their pending flags without indexing anything,
        // and DrainAllPendingAsync needs that to still count as progress so it keeps going.
        return resolved;
    }

    /// <summary>
    /// One-shot catch-up: repeatedly runs <see cref="ProcessPendingAsync"/> back-to-back (no
    /// inter-batch delay) until a batch makes no progress -- either because nothing is left
    /// pending, or because every remaining item is failing (in which case the periodic
    /// <see cref="ExecuteAsync"/> loop will keep retrying it on its normal schedule). Bounded by
    /// <paramref name="maxBatches"/> as a hard safety cap, not a target -- normal drains stop far
    /// earlier via the zero-progress check.
    /// </summary>
    public async Task<int> DrainAllPendingAsync(CancellationToken ct, int maxBatches = 200)
    {
        var total = 0;
        for (var i = 0; i < maxBatches && !ct.IsCancellationRequested; i++)
        {
            var resolved = await ProcessPendingAsync(ct);
            if (resolved == -1)
            {
                // Lock held by the periodic tick or a concurrent drain -- that's still making
                // progress, just not on this thread. Back off briefly and try again rather than
                // giving up as if there were nothing left pending.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (resolved == 0) break;
            total += resolved;
        }
        return total;
    }
}

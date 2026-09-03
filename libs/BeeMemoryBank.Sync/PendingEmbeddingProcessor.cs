using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Background service for processing articles with pending embeddings.
/// Runs only on nodes with can_generate_embeddings = true.
/// Generates and saves embedding projections for articles with embedding_pending = true.
/// </summary>
public class PendingEmbeddingProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<PendingEmbeddingProcessor> logger,
    TimeSpan? interval = null,
    int? batchSize = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(5);
    private readonly int _batchSize = batchSize ?? 50;

    // Guards against the periodic tick and a manual DrainAllPendingAsync (or two concurrent
    // manual drains) running ProcessPendingCoreAsync at the same time. Without this, two
    // concurrent runs could pull and re-process the same pending batch. AGY review 2026-08-12
    // caught the search-index equivalent of this hazard producing duplicate segments that
    // permanently crash the merge step -- see PendingIndexProcessor for the worse case; this
    // processor doesn't have that specific failure mode, but the same non-exclusive access
    // would still mean duplicate embedding-generation work.
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
            catch (ModelUnavailableException ex)
            {
                logger.LogInformation("Embedding model is unavailable. Skipping pending embedding processing. Details: {Message}", ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error processing pending embeddings");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one batch. Returns the number processed, 0 if there was nothing to do, or -1 if
    /// another run (the periodic tick or a concurrent drain) is already in flight -- callers that
    /// care about the difference (see <see cref="DrainAllPendingAsync"/>) should back off and
    /// retry rather than treating -1 the same as "nothing pending".
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

        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync();
        if (identity == null || !identity.CanGenerateEmbeddings) return 0;

        // Concept tag embeddings don't require session unlock — backfill unconditionally
        var conceptTagService = scope.ServiceProvider.GetRequiredService<ConceptTagService>();
        await conceptTagService.BackfillEmbeddingsAsync();

        // Article projections require an unlocked session (projection matrix is encrypted)
        if (!session.IsUnlocked) return 0;

        var articleRepo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
        var bodyRepo = scope.ServiceProvider.GetRequiredService<IArticleBodyRepository>();
        var projectionService = scope.ServiceProvider.GetRequiredService<EmbeddingProjectionService>();

        await projectionService.EnsureProjectionMatrixAsync();

        // Idempotent no-op after the model version last changed: re-flags any article embedded by
        // a since-replaced model version so it gets picked up below instead of silently staying on
        // stale vectors (dimension-based staleness checks elsewhere don't catch a same-dimension
        // model swap). ConceptTagService.BackfillEmbeddingsAsync does the equivalent check for
        // concept tags on its own call a few lines up.
        await articleRepo.MarkStaleEmbeddingsPendingUnscopedAsync(OnnxEmbeddingGenerator.Version);

        var pending = await articleRepo.GetEmbeddingPendingAsync(_batchSize);
        if (pending.Count == 0) return 0;

        logger.LogInformation("Processing {Count} articles with pending embeddings", pending.Count);

        int processed = 0;
        foreach (var article in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (!session.IsUnlocked) break;
            try
            {
                var body = await bodyRepo.GetByArticleIdAsync(article.Id);
                if (body == null) continue;

                // Decrypt the body for embedding generation
                var articleService = scope.ServiceProvider.GetRequiredService<ArticleService>();
                var plaintext = await articleService.GetContentAsync(article.Id);

                await projectionService.ProjectArticleAsync(article, plaintext);
                processed++;
            }
            catch (ModelUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process article {ArticleId}", article.Id);
            }
        }

        if (processed > 0)
            logger.LogInformation("Processed embeddings: {Count}", processed);

        return processed;
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
            var processed = await ProcessPendingAsync(ct);
            if (processed == -1)
            {
                // Lock held by the periodic tick or a concurrent drain -- that's still making
                // progress, just not on this thread. Back off briefly and try again rather than
                // giving up as if there were nothing left pending.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (processed == 0) break;
            total += processed;
        }
        return total;
    }
}

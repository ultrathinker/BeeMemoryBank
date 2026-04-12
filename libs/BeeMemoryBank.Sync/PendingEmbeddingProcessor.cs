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
    TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(5);

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
                logger.LogError(ex, "Error processing pending embeddings");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();

        // Only work when the session is unlocked
        if (!session.IsUnlocked) return;

        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync();
        if (identity == null || !identity.CanGenerateEmbeddings) return;

        var articleRepo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
        var bodyRepo = scope.ServiceProvider.GetRequiredService<IArticleBodyRepository>();
        var projectionService = scope.ServiceProvider.GetRequiredService<EmbeddingProjectionService>();

        await projectionService.EnsureProjectionMatrixAsync();

        var pending = await articleRepo.GetEmbeddingPendingAsync(50);
        if (pending.Count == 0) return;

        logger.LogInformation("Processing {Count} articles with pending embeddings", pending.Count);

        int processed = 0;
        foreach (var article in pending)
        {
            if (ct.IsCancellationRequested) break;
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process article {ArticleId}", article.Id);
            }
        }

        if (processed > 0)
            logger.LogInformation("Processed embeddings: {Count}", processed);
    }
}

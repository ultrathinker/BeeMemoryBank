using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Background periodic cleanup service:
/// — purges ciphertexts of soft-deleted articles after 30 days
/// — deletes expired tombstones (60 days)
/// — deletes expired conflict versions (7 days)
/// — hard-deletes soft-deleted comments after 90 days
/// </summary>
public class CleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<CleanupService> logger,
    TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First run 10 seconds after startup
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during cleanup");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var now = DateTime.UtcNow;

        var bodyRepo = scope.ServiceProvider.GetRequiredService<IArticleBodyRepository>();
        var tombstoneRepo = scope.ServiceProvider.GetRequiredService<ITombstoneRepository>();
        var conflictRepo = scope.ServiceProvider.GetRequiredService<IConflictVersionRepository>();

        // Purge ciphertexts for articles soft-deleted more than 30 days ago
        var ciphertextCutoff = now.AddDays(-30);
        var purged = await bodyRepo.PurgeForDeletedArticlesOlderThanAsync(ciphertextCutoff);
        if (purged > 0)
            logger.LogInformation("Cleanup: purged {Count} ciphertexts (articles deleted before {Cutoff:yyyy-MM-dd})",
                purged, ciphertextCutoff);

        // Delete expired tombstones (older than 60 days)
        var tombstonesDeleted = await tombstoneRepo.DeleteExpiredAsync(now);
        if (tombstonesDeleted > 0)
            logger.LogInformation("Cleanup: deleted {Count} expired tombstones", tombstonesDeleted);

        // Delete expired conflict versions (older than 7 days)
        var conflictsDeleted = await conflictRepo.DeleteExpiredAsync(now);
        if (conflictsDeleted > 0)
            logger.LogInformation("Cleanup: deleted {Count} expired conflict versions", conflictsDeleted);

        // Purge soft-deleted comments older than 90 days
        var commentRepo = scope.ServiceProvider.GetRequiredService<ICommentRepository>();
        var commentsCutoff = now.AddDays(-90);
        var purgedComments = await commentRepo.PurgeSoftDeletedOlderThanAsync(commentsCutoff);
        if (purgedComments > 0)
            logger.LogInformation("Cleanup: purged {Count} soft-deleted comments (deleted before {Cutoff:yyyy-MM-dd})",
                purgedComments, commentsCutoff);

        // Purge media for soft-deleted articles (30 days)
        await CleanupMediaAsync(scope, now);
    }

    private async Task CleanupMediaAsync(IServiceScope scope, DateTime now)
    {
        var mediaRepo = scope.ServiceProvider.GetService<IMediaRepository>();
        if (mediaRepo == null) return; // media feature not registered

        var mediaOptions = scope.ServiceProvider.GetService<Core.Services.MediaStorageOptions>();
        var mediaDir = mediaOptions?.MediaDir;
        if (string.IsNullOrEmpty(mediaDir)) return;

        // Purge soft-deleted media (article was deleted > 30 days ago)
        var deletedMedia = await mediaRepo.GetDeletedOlderThanAsync(now.AddDays(-30));
        foreach (var m in deletedMedia)
        {
            await mediaRepo.DeleteByIdAsync(m.Id);
            var encPath = Path.Combine(mediaDir, $"{m.Id}.enc");
            try { File.Delete(encPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(encPath))
                    logger.LogWarning(ex, "Media file already gone during cleanup: {Path}", encPath);
            }
        }
        if (deletedMedia.Count > 0)
            logger.LogInformation("Cleanup: purged {Count} deleted media files", deletedMedia.Count);

        // Purge orphaned media (uploaded but never linked to an article, > 24 hours)
        var orphans = await mediaRepo.GetOrphanedOlderThanAsync(now.AddHours(-24));
        foreach (var m in orphans)
        {
            await mediaRepo.DeleteByIdAsync(m.Id);
            var encPath = Path.Combine(mediaDir, $"{m.Id}.enc");
            try { File.Delete(encPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(encPath))
                    logger.LogWarning(ex, "Orphan media file already gone during cleanup: {Path}", encPath);
            }
        }
        if (orphans.Count > 0)
            logger.LogInformation("Cleanup: purged {Count} orphaned media files", orphans.Count);
    }
}

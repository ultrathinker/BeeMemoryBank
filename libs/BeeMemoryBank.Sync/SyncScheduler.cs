using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Background service that periodically synchronizes with all active nodes from the whitelist.
/// Default interval is 60 seconds. Uses ISyncTrigger for push-on-save (near-realtime sync).
/// </summary>
public class SyncScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncScheduler> logger,
    ISyncTrigger syncTrigger,
    IHttpClientFactory httpClientFactory,
    TimeSpan? interval = null,
    Action? periodicCleanup = null) : BackgroundService
{
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromSeconds(60);
    public event EventHandler<SyncCycleResult>? SyncCycleCompleted;

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncScheduler started, interval {Interval}", Interval);

        // First sync after a short delay on startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _syncLock.WaitAsync(stoppingToken);
                try
                {
                    var result = await SyncAllAsync(stoppingToken);
                    SyncCycleCompleted?.Invoke(this, result);
                }
                finally
                {
                    _syncLock.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SyncScheduler cycle failed, will retry next interval");
            }

            await syncTrigger.WaitAsync(Interval, stoppingToken);
        }
    }

    private async Task<SyncCycleResult> SyncAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        
        var invisibleMode = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.InvisibleModeService>();
        if (invisibleMode.IsInvisible) return new SyncCycleResult(0);

        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();

        periodicCleanup?.Invoke();

        int totalApplied = 0;
        List<Core.Models.WhitelistEntry> nodes;
        try
        {
            nodes = await whitelist.GetAllActiveAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving whitelist");
            return new SyncCycleResult(0);
        }

        // Only sync with nodes that have an API address configured
        var remoteNodes = nodes.Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();
        if (remoteNodes.Count == 0) return new SyncCycleResult(0);

        using var http = httpClientFactory.CreateClient("SyncScheduler");

        foreach (var node in remoteNodes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                totalApplied += await syncClient.SyncWithAsync(http, node.ApiAddress!, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error synchronizing with {NodeId} ({Address})",
                    node.NodeId, node.ApiAddress);
            }
        }

        return new SyncCycleResult(totalApplied);
    }
}

public record SyncCycleResult(int TotalApplied);

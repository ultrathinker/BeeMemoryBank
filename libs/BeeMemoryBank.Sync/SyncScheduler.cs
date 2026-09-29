using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
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
    Action? periodicCleanup = null,
    SnapshotRequiredState? snapshotRequiredState = null) : BackgroundService
{
    /// <summary>The named HttpClient the scheduler syncs with; pinned by AddSync (UsePinnedSyncHandler).</summary>
    public const string HttpClientName = "SyncScheduler";

    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shortest time between the starts of two cycles. Every save signals the trigger and every
    /// cycle opens with a fresh challenge/authenticate handshake, which the peer rate-limits per IP
    /// (30 a minute, shared with anything else behind the same NAT, such as a phone). Saving about
    /// once a second used to start a cycle per save and draw 429s for minutes on end (seen on the
    /// test stand, BMB-31 scenario 12); with a gap, a burst of saves rides one cycle.
    /// </summary>
    public TimeSpan MinCycleGap { get; set; } = TimeSpan.FromSeconds(3);
    public event EventHandler<SyncCycleResult>? SyncCycleCompleted;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly UnreachablePeers _unreachable = new();
    private bool _disposed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncScheduler started, interval {Interval}", Interval);

        // Before anything is served or pushed from the log: hosts without the API startup (mobile)
        // run the repair here. See StoredEventRepair; a second run elsewhere finds nothing.
        try { await StoredEventRepair.RunAsync(scopeFactory, logger); }
        catch (Exception ex) { logger.LogError(ex, "Stored event repair failed; retried at the next start"); }

        // First sync after a short delay on startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var cycleStart = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Every cycle, invisible or not: a repair scrub still owed is finished as soon as nothing
            // holds its checkpoint back (see StoredEventRepair.RetryPendingScrubAsync).
            try { await StoredEventRepair.RetryPendingScrubAsync(scopeFactory, logger); }
            catch (Exception ex) { logger.LogWarning(ex, "Retrying the stored event repair's scrub failed; retried next cycle"); }

            try
            {
                await _syncLock.WaitAsync(stoppingToken);
                cycleStart = DateTime.UtcNow;
                try
                {
                    var result = await SyncAllAsync(stoppingToken);
                    // Guard: a throwing subscriber must not bubble into the outer catch and fire the
                    // error-path event too (double-fire for one cycle).
                    try { SyncCycleCompleted?.Invoke(this, result); }
                    catch (Exception ex) { logger.LogWarning(ex, "SyncCycleCompleted subscriber threw"); }
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
                // Still signal completion (with the error) so heartbeat subscribers can tell a
                // failed-but-alive loop apart from a loop that stopped firing entirely.
                SyncCycleCompleted?.Invoke(this, new SyncCycleResult(0, ex.Message));
            }

            await syncTrigger.WaitAsync(Interval, stoppingToken);
            var gap = RemainingGap(cycleStart, DateTime.UtcNow, MinCycleGap);
            if (gap > TimeSpan.Zero)
                await Task.Delay(gap, stoppingToken);
        }
    }

    /// <summary>How long to hold off before the next cycle so it starts no sooner than
    /// <paramref name="minGap"/> after the previous one started.</summary>
    public static TimeSpan RemainingGap(DateTime lastStartUtc, DateTime nowUtc, TimeSpan minGap)
    {
        var left = lastStartUtc + minGap - nowUtc;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    internal async Task<SyncCycleResult> SyncAllAsync(CancellationToken ct)
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

        using var http = httpClientFactory.CreateClient(HttpClientName);
        var reseeder = scope.ServiceProvider.GetService<IBlindPeerReseeder>();

        foreach (var node in remoteNodes)
        {
            if (ct.IsCancellationRequested) break;
            if (_unreachable.ShouldSkip(node.NodeId, DateTime.UtcNow))
                continue;

            Exception? failure = null;
            try
            {
                totalApplied += await syncClient.SyncWithPeerAsync(http, node.ApiAddress!, node.NodeId, ct);
                snapshotRequiredState?.Clear();
                if (_unreachable.NoteSuccess(node.NodeId) is var failedBefore and > 0)
                    logger.LogInformation("{NodeId} ({Address}) is reachable again after {Failures} failed attempts",
                        node.NodeId, node.ApiAddress, failedBefore);
            }
            catch (PushGapException ex)
            {
                // The PEER is behind our compaction, not us: nothing to flag locally. A blind peer
                // is reseeded below; a full one has to rejoin.
                failure = ex;
                logger.LogWarning("{NodeId} ({Address}) missed events our compaction removed (pushed up to {Pushed}, cp={Cp})",
                    node.NodeId, node.ApiAddress, ex.PushedUpTo, ex.LastCompactionCp);
            }
            catch (SnapshotRequiredException ex) when (BlindNodeId.IsBlind(node.NodeId))
            {
                // A blind node refused our pull, so it is the blind node's log that starts above our position: this node
                // is not out of sync, and a wipe of it would cure nothing. SyncWithPeerAsync adopts the blind node's
                // checkpoint itself, so this is what is left when even that did not help (a checkpoint that moved again).
                failure = ex;
                logger.LogCritical(
                    "Blind node {NodeId} ({Url}) refused our pull: its log starts above our position (its checkpoint cp={Cp}, head={Head}). " +
                    "This node is not out of sync and is not to be wiped; reseed the blind node from the Blind nodes page.",
                    node.NodeId, ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq);
            }
            catch (SnapshotRequiredException ex)
            {
                failure = ex;
                logger.LogCritical(
                    "Node is out-of-sync with {Url}: compacted past us (cp={Cp}, head={Head}). Manual wipe & rejoin required.",
                    ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq);
                snapshotRequiredState?.Set(ex);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct))
            {
                failure = ex;
                NoteUnreachable(node, ex);
                continue;
            }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogWarning(ex, "Error synchronizing with {NodeId} ({Address})",
                    node.NodeId, node.ApiAddress);
            }

            if (reseeder != null && BlindNodeId.IsBlind(node.NodeId))
            {
                try { await reseeder.AfterSyncAsync(node, http, failure, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Reseeding blind node {NodeId} ({Address}) failed", node.NodeId, node.ApiAddress);
                }
            }
        }

        return new SyncCycleResult(totalApplied);
    }

    /// <summary>Could not talk to the peer at all — as opposed to talking and being refused.</summary>
    internal static bool IsUnreachable(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        TaskCanceledException when !ct.IsCancellationRequested => true, // the HttpClient timeout
        _ => false
    };

    /// <summary>
    /// Only the first failure of a streak is a warning, the rest go to debug — so an absent phone
    /// does not fill the log with the same line every minute.
    /// </summary>
    private void NoteUnreachable(WhitelistEntry node, Exception ex)
    {
        var (failures, pause) = _unreachable.NoteFailure(node.NodeId, DateTime.UtcNow, Interval);
        if (failures == 1)
            logger.LogWarning("{NodeId} ({Address}) is unreachable ({Error}); retrying with a growing pause, up to {Max}",
                node.NodeId, node.ApiAddress, ex.Message, UnreachablePeers.MaxPause);
        else
            logger.LogDebug("{NodeId} ({Address}) still unreachable ({Failures} attempts); next try in {Pause}",
                node.NodeId, node.ApiAddress, failures, pause);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _syncLock.Dispose();
        base.Dispose();
    }
}

public record SyncCycleResult(int TotalApplied, string? Error = null);

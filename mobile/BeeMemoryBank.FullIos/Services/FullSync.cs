using System.Net;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>How the last contact with one peer went.</summary>
public sealed record PeerStatus(Guid NodeId, string Name, string Address, bool Pinned, DateTime? LastSuccess, DateTime? LastAttempt, string? Problem)
{
    /// <summary>One line for the screen: the last contact, whether the key is pinned, the last problem.</summary>
    public string Summary =>
        (LastSuccess is { } ok ? $"Last contact {ok.ToLocalTime():HH:mm:ss}" : "No contact yet since the app opened") +
        (Pinned ? " · key pinned" : "") +
        (Problem is not null && (LastSuccess is null || LastAttempt > LastSuccess) ? " · " + Problem : "");
}

/// <summary>One sync round: notes and changes applied here, peers reached, the first problem.</summary>
public sealed record SyncRoundResult(DateTime At, int Applied, int Reached, int Failed, string? Problem)
{
    public static SyncRoundResult Nothing(DateTime at) => new(at, 0, 0, 0, null);
}

/// <summary>
/// The phone as a sync client. Every round calls each active peer that has an address - SyncClient.SyncWithPeerAsync, the same push,
/// pull and blob exchange as every node, protocol unchanged - over the app's default HTTP client, which pins the peers that have a pinned
/// key and refuses redirects (FullNodeServices). Nothing listens on the phone: peers never call it. Rounds run while the app is open and
/// the vault unlocked: at once, after every local write (the event logger's sync signal), and every <see cref="Interval"/>; one more
/// round is asked for when the app leaves the screen (<see cref="RoundAsync"/>, inside the seconds iOS grants). Only one round runs at a
/// time.
/// </summary>
public sealed class FullSync(IServiceProvider services, ILogger<FullSync> logger)
{
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly Dictionary<Guid, (DateTime? Success, DateTime? Attempt, string? Problem)> _peers = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private bool _repaired;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The shortest time between two rounds the loop starts (a burst of saves is one round).</summary>
    public TimeSpan MinGap { get; set; } = TimeSpan.FromSeconds(3);

    public SyncRoundResult? LastRound { get; private set; }

    public bool IsRunning => _round.CurrentCount == 0;

    /// <summary>A round started or ended: the screens refresh.</summary>
    public event Action? Changed;

    /// <summary>One round now; waits for a running one to end first. Never throws for a peer's problem: it is in the result.</summary>
    public async Task<SyncRoundResult> RoundAsync(CancellationToken ct = default)
    {
        await _round.WaitAsync(ct);
        Notify();
        try
        {
            var result = await RunRoundAsync(ct);
            LastRound = result;
            return result;
        }
        finally
        {
            _round.Release();
            Notify();
        }
    }

    /// <summary>Starts the loop (once); <see cref="StopAsync"/> ends it.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            _loopTask = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>Ends the loop and waits for a running round to end (it is cancelled).</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? loop;
        Task? task;
        lock (_gate)
        {
            loop = _loop;
            task = _loopTask;
            _loop = null;
            _loopTask = null;
        }
        if (loop is null) return;
        loop.Cancel();
        try { if (task is not null) await task; }
        catch (OperationCanceledException) { }
        finally { loop.Dispose(); }
    }

    /// <summary>Every active peer this phone syncs with, and how its last contact went.</summary>
    public async Task<IReadOnlyList<PeerStatus>> PeersAsync()
    {
        using var scope = services.CreateScope();
        var self = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
        var rows = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        lock (_gate)
        {
            return rows
                .Where(r => r.NodeId != self && !string.IsNullOrEmpty(r.ApiAddress))
                .Select(r =>
                {
                    _peers.TryGetValue(r.NodeId, out var seen);
                    return new PeerStatus(r.NodeId, r.DisplayName, r.ApiAddress!, r.EffectivePin is not null, seen.Success, seen.Attempt, seen.Problem);
                })
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var trigger = services.GetRequiredService<ISyncTrigger>();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await RoundAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sync round failed");
            }

            try
            {
                await trigger.WaitAsync(Interval, ct);
                var gap = SyncScheduler.RemainingGap(started, DateTime.UtcNow, MinGap);
                if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<SyncRoundResult> RunRoundAsync(CancellationToken ct)
    {
        var at = DateTime.UtcNow;
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        if (!_repaired)
        {
            // What the product's scheduler does once per start: scrub events a damaged earlier version stored.
            try { await StoredEventRepair.RunAsync(scopes, logger); _repaired = true; }
            catch (Exception ex) { logger.LogError(ex, "Stored event repair failed; retried at the next round"); }
        }
        try { await StoredEventRepair.RetryPendingScrubAsync(scopes, logger); }
        catch (Exception ex) { logger.LogWarning(ex, "Retrying the stored event repair's scrub failed"); }

        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<InvisibleModeService>().IsInvisible) return SyncRoundResult.Nothing(at);

        var peers = await PeersAsync();
        if (peers.Count == 0) return SyncRoundResult.Nothing(at);

        var client = scope.ServiceProvider.GetRequiredService<SyncClient>();
        using var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        int applied = 0, reached = 0, failed = 0;
        string? firstProblem = null;
        foreach (var peer in peers)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = DateTime.UtcNow;
            try
            {
                applied += await client.SyncWithPeerAsync(http, peer.Address, peer.NodeId, ct);
                reached++;
                Remember(peer.NodeId, attempt, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                var problem = Describe(ex, peer);
                firstProblem ??= $"{peer.Name}: {problem}";
                Remember(peer.NodeId, attempt, problem);
                logger.LogWarning("Sync with {Peer} ({Address}) failed: {Problem}", peer.NodeId, peer.Address, problem);
            }
        }
        return new SyncRoundResult(at, applied, reached, failed, firstProblem);
    }

    private void Remember(Guid peer, DateTime attempt, string? problem)
    {
        lock (_gate)
        {
            _peers.TryGetValue(peer, out var seen);
            _peers[peer] = problem is null ? (attempt, attempt, null) : (seen.Success, attempt, problem);
        }
    }

    /// <summary>
    /// What went wrong, in words for the screen. Never a request's content; the exception's own text only where it is the network
    /// stack's (no secret is in it).
    /// </summary>
    public static string Describe(Exception ex, PeerStatus peer) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } when peer.Pinned =>
            "it answered with another key than the one this iPhone pinned when it joined, so nothing was sent.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "its certificate is not trusted (a node on your network is joined with its join code, which pins its key).",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "it no longer accepts this iPhone (was it removed from the network?).",
        HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } => "it is busy or under maintenance; next round.",
        HttpRequestException { StatusCode: null } => "not reachable (is it on, and is this iPhone on the same network?).",
        HttpRequestException { StatusCode: { } code } => $"it answered {(int)code}.",
        TaskCanceledException => "it did not answer in time.",
        SnapshotRequiredException => "this copy is too far behind that node to catch up by syncing; join again to start fresh.",
        _ => ex.Message,
    };

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { logger.LogWarning(ex, "A sync status listener failed"); }
    }
}

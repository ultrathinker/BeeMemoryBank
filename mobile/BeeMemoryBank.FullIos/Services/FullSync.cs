using System.Net;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>How the last contact with one peer went.</summary>
/// <param name="Address">The address the next session tries first (or the peer's own address when it has no channel yet).</param>
/// <param name="Reachability">The peer's state in <see cref="ReachabilityStates"/>, as far as this app run has seen it.</param>
/// <param name="ProblemCode">The kind of the last failure (<see cref="FullSync"/> codes), for the screen's own words; null when the last contact was fine.</param>
public sealed record PeerStatus(Guid NodeId, string Name, string Address, bool Pinned, DateTime? LastSuccess, DateTime? LastAttempt, string? ProblemCode,
    string Reachability = ReachabilityStates.Unknown, string? ReachabilityText = null)
{
    public bool HasReachabilityText => !string.IsNullOrEmpty(ReachabilityText);

    /// <summary>One line for the screen: the last contact and whether the key is pinned (the problem is said in the app's own words elsewhere).</summary>
    public string Summary =>
        (LastSuccess is { } ok ? $"Last contact {ok.ToLocalTime():HH:mm:ss}" : "No contact yet since the app opened") +
        (Pinned ? " · key pinned" : "");
}

/// <summary>One sync round: notes and changes applied here, peers reached, real failures and unreachable peers kept apart.</summary>
/// <param name="Problem">The first REAL failure's code (a refusal, a wrong key, a broken copy); null when nothing failed for real.</param>
/// <param name="Unreachable">How many peers simply could not be reached (timeout, refused connection, no route, no answer, DNS): not an error.</param>
/// <param name="UnreachablePeer">The first unreachable peer's name, for the yellow "not reachable for now" line.</param>
public sealed record SyncRoundResult(DateTime At, int Applied, int Reached, int Failed, string? Problem, int Unreachable = 0, string? UnreachablePeer = null)
{
    public static SyncRoundResult Nothing(DateTime at) => new(at, 0, 0, 0, null);
}

/// <summary>
/// The phone as a sync client. Every round calls each active peer that has a way to be called now - PeerSessions.SyncWithPeerAsync: the peer's
/// channels (its address, what it announced, where Bonjour showed it, what a person typed) in the planner's order, its TLS anchor on every request,
/// then the same push, pull and blob exchange as every node, protocol unchanged - over the app's default HTTP client, which pins the peers that have
/// a pinned key and refuses redirects (FullNodeServices). While the loop runs, <see cref="BonjourPeerDiscovery"/> looks for the peers' computers
/// on the Wi-Fi. Nothing listens on the phone: peers never call it. Rounds run while the app is open and
/// the vault unlocked: at once, after every local write (the event logger's sync signal), and every <see cref="Interval"/>; one more
/// round is asked for when the app leaves the screen (<see cref="RoundAsync"/>, inside the seconds iOS grants). Only one round runs at a
/// time.
/// </summary>
public sealed class FullSync(IServiceProvider services, ILogger<FullSync> logger)
{
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly record struct Seen(DateTime? Success, DateTime? Attempt, string? ProblemCode, DateTime? FailingSince);

    /// <summary>Problem codes the screens turn into their own words. "unreachable" is not an error: the badge stays yellow.</summary>
    public const string ProblemUnreachable = "unreachable";
    public const string ProblemWrongKey = "wrongkey";
    public const string ProblemRefused = "refused";
    public const string ProblemBusy = "busy";
    public const string ProblemSnapshot = "snapshot";
    public const string ProblemOther = "other";
    /// <summary>A computer proved that an administrator disconnected this phone from the bank (2.12): one calm sentence, not an error.</summary>
    public const string ProblemDisconnected = "disconnected";

    /// <summary>While this phone is disconnected, the automatic rounds ask the computers again only this often (a round the person asks for always runs).</summary>
    public static readonly TimeSpan DisconnectedRetry = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether a failed contact is "the computer is not reachable for now" (yellow) or a real failure (red). A timeout, a refused
    /// connection, no route, no answer or a DNS failure only mean the peer is away; a refusal (401/403), a wrong pinned key or a
    /// broken copy are real failures. Reads only the exception the sync layer already threw; libs is not changed.
    /// </summary>
    public static string Classify(Exception ex, bool pinned) => ex switch
    {
        ThisDeviceRevokedException => ProblemDisconnected,
        TaskCanceledException or OperationCanceledException => ProblemUnreachable,
        System.Net.Sockets.SocketException => ProblemUnreachable,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => pinned ? ProblemWrongKey : ProblemUnreachable,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => ProblemRefused,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } => ProblemBusy,
        HttpRequestException { StatusCode: null } => ProblemUnreachable,
        SnapshotRequiredException => ProblemSnapshot,
        _ => ProblemOther,
    };

    private readonly Dictionary<Guid, Seen> _peers = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private bool _repaired;
    private bool _wired;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The shortest time between two rounds the loop starts (a burst of saves is one round).</summary>
    public TimeSpan MinGap { get; set; } = TimeSpan.FromSeconds(3);

    public SyncRoundResult? LastRound { get; private set; }

    /// <summary>
    /// Since when a computer has said that an administrator disconnected this phone (it proved it with its signature); null while none has, and
    /// again after any session goes through (the device was added back). What is on the phone stays readable; it no longer receives changes.
    /// </summary>
    public DateTime? DisconnectedSince { get; private set; }

    private DateTime? _disconnectedAskedAt;

    /// <summary>Test seam: the session with one peer (the product's <see cref="PeerSessions.SyncWithPeerAsync"/> when null).</summary>
    internal Func<IServiceProvider, HttpClient, WhitelistEntry, CancellationToken, Task<int>>? SessionForTests { get; set; }

    public bool IsRunning => _round.CurrentCount == 0;

    /// <summary>A round started or ended: the screens refresh.</summary>
    public event Action? Changed;

    /// <summary>
    /// One round now; waits for a running one to end first. Never throws for a peer's problem: it is in the result. <paramref name="automatic"/>:
    /// the loop's own round (and the last one before the background), which a disconnected phone runs only every <see cref="DisconnectedRetry"/>.
    /// </summary>
    public async Task<SyncRoundResult> RoundAsync(CancellationToken ct = default, bool automatic = false)
    {
        await _round.WaitAsync(ct);
        Notify();
        try
        {
            var result = automatic && DisconnectedSince is not null && _disconnectedAskedAt is { } asked && DateTime.UtcNow - asked < DisconnectedRetry
                ? new SyncRoundResult(DateTime.UtcNow, 0, 0, 0, ProblemDisconnected)
                : await RunRoundAsync(ct);
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
        WireDiscovery();
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
        finally
        {
            loop.Dispose();
            // The browse of the local network lives as long as the loop: it is off in the background and while the vault is locked.
            Discovery?.Stop();
        }
    }

    /// <summary>What iOS lets the app do on the local network; <see cref="LocalNetworkAccess.Unknown"/> until the first browse answers.</summary>
    public LocalNetworkAccess LocalNetwork => Discovery?.Access ?? LocalNetworkAccess.Unknown;

    /// <summary>The sentence for a refused local network (with the way back), or null.</summary>
    public string? LocalNetworkNotice => LocalNetwork == LocalNetworkAccess.Denied ? LanStatusText.LocalNetworkOff : null;

    private BonjourPeerDiscovery? Discovery => services.GetService<BonjourPeerDiscovery>();

    private bool LoopRunning
    {
        get { lock (_gate) return _loop is not null; }
    }

    private void WireDiscovery()
    {
        if (_wired || Discovery is not { } discovery) return;
        _wired = true;
        discovery.Changed += Notify;
    }

    /// <summary>Every active peer this phone has a way to reach (an address of its own, one it announced, one Bonjour showed or a person typed), and how its last contact went.</summary>
    public async Task<IReadOnlyList<PeerStatus>> PeersAsync()
    {
        using var scope = services.CreateScope();
        return (await PeersOfAsync(scope.ServiceProvider)).Select(p => p.Status).ToList();
    }

    /// <summary>
    /// The peers with their whitelist rows and channel plans: LAN peers of the network this phone is on first, then by name. A peer with no way to be
    /// reached from here (it calls us, or never gave an address) is not listed. The plan of every peer is noted for its reachability, also when nothing
    /// can be dialled now (a computer that is at home, while this phone is not).
    /// </summary>
    private async Task<List<(WhitelistEntry Row, ChannelPlan Plan, PeerStatus Status)>> PeersOfAsync(IServiceProvider sp)
    {
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
        var rows = await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        var reachability = sp.GetService<PeerReachability>();
        var now = DateTime.UtcNow;
        var peers = new List<(WhitelistEntry, ChannelPlan, PeerStatus)>();
        foreach (var row in rows.Where(r => r.NodeId != self))
        {
            var (plan, _) = await PeerSessions.PlanAsync(sp, row, logger);
            if (plan.All.Count == 0) continue;
            reachability?.NotePlan(row.NodeId, plan, now);
            peers.Add((row, plan, StatusOf(row, plan, reachability, now)));
        }
        return peers
            .OrderByDescending(p => p.Item2.AnyLanOnCurrentNetwork)
            .ThenBy(p => p.Item3.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private PeerStatus StatusOf(WhitelistEntry row, ChannelPlan plan, PeerReachability? reachability, DateTime now)
    {
        Seen seen;
        lock (_gate) _peers.TryGetValue(row.NodeId, out seen);
        var address = plan.All.FirstOrDefault()?.Url ?? row.ApiAddress ?? "";
        var state = ReachabilityStates.Unknown;
        if (reachability is not null)
        {
            DateTime? directAt = null;
            string? directScope = null;
            if (reachability.DirectOf(row.NodeId) is { } direct) (directAt, directScope) = direct;
            // The phone is never called, and asks no other peer about this one: its states are the direct ones, "away", and "not answering".
            state = PeerReachability.Compute(
                new PeerReachability.Inputs(TimeSpan.FromTicks(Interval.Ticks * 5 / 2), directAt, directScope, null, seen.FailingSince,
                    reachability.RelayOf(row.NodeId), null, reachability.PlanOf(row.NodeId), null), now);
        }
        return new PeerStatus(row.NodeId, row.DisplayName, address, row.EffectivePin is not null, seen.Success, seen.Attempt, seen.ProblemCode,
            state, LanStatusText.Of(state, row.DisplayName));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var trigger = services.GetRequiredService<ISyncTrigger>();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await RoundAsync(ct, automatic: true);
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

        // While the loop runs, the browse of the local network is on and looks again where the last round found an address gone. The last round
        // before the background (AppFlow.Stopped) runs after the loop has ended and does not start it again.
        if (LoopRunning && Discovery is { } discovery)
        {
            try { await discovery.PrepareRoundAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Looking for the peers on the local network failed"); }
        }

        var peers = await PeersOfAsync(scope.ServiceProvider);
        if (peers.Count == 0) return SyncRoundResult.Nothing(at);

        using var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        int applied = 0, reached = 0, failed = 0, unreachable = 0;
        string? firstProblem = null;
        var disconnected = false;
        string? firstUnreachable = null;
        foreach (var (row, plan, peer) in peers)
        {
            ct.ThrowIfCancellationRequested();
            // Nothing to dial now (its addresses are on networks this phone is not on): the card says "Not on this network", and the round goes on.
            if (!plan.HasDialable) continue;
            var attempt = DateTime.UtcNow;
            try
            {
                applied += SessionForTests is { } session
                    ? await session(scope.ServiceProvider, http, row, ct)
                    : await PeerSessions.SyncWithPeerAsync(scope.ServiceProvider, http, row, ct, logger);
                reached++;
                Remember(peer.NodeId, attempt, null);
                if (DisconnectedSince is not null)
                {
                    DisconnectedSince = null;
                    logger.LogInformation("{Peer} accepts this phone again", peer.NodeId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var code = Classify(ex, peer.Pinned);
                if (code == ProblemDisconnected)
                {
                    // Said once, calmly (2.12): the sentence is on the sync screen, and the loop asks again only every DisconnectedRetry.
                    failed++;
                    firstProblem = code;
                    Remember(peer.NodeId, attempt, code);
                    if (!disconnected && DisconnectedSince is null) logger.LogInformation("{Peer} says an administrator disconnected this phone from the bank", peer.NodeId);
                    disconnected = true;
                    continue;
                }
                if (code == ProblemUnreachable)
                {
                    // A computer that is simply away is not an error: yellow, never red.
                    unreachable++;
                    firstUnreachable ??= peer.Name;
                }
                else
                {
                    failed++;
                    firstProblem ??= code;
                }
                Remember(peer.NodeId, attempt, code);
                logger.LogWarning("Sync with {Peer} failed ({Code}): {Problem}", peer.NodeId, code, Describe(ex, peer));
            }
        }
        if (disconnected && reached == 0)
        {
            DisconnectedSince ??= at;
            _disconnectedAskedAt = at;
        }
        return new SyncRoundResult(at, applied, reached, failed, firstProblem, unreachable, firstUnreachable);
    }

    private void Remember(Guid peer, DateTime attempt, string? code)
    {
        lock (_gate)
        {
            _peers.TryGetValue(peer, out var seen);
            _peers[peer] = code is null
                ? new Seen(attempt, attempt, null, null)
                : new Seen(seen.Success, attempt, code, seen.FailingSince ?? attempt);
        }
    }

    /// <summary>
    /// What went wrong, in words for the screen. Never a request's content; the exception's own text only where it is the network
    /// stack's (no secret is in it).
    /// </summary>
    public static string Describe(Exception ex, PeerStatus peer) => ex switch
    {
        ThisDeviceRevokedException => ThisDeviceRevokedException.Sentence,
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

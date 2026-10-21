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
    SnapshotRequiredState? snapshotRequiredState = null) : BackgroundService, IPeerStateResetter
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

    // The address each peer was called at the last time this loop looked (PeerContactLog.EndpointKey). What was learned at an address - the pause of an
    // unreachable peer, the closed window, the failure reason, the contact record - is not the next address's: it is forgotten when the address changes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _endpoints = new();

    // The manual checks (CheckPeerNowAsync): which peers have one running, decided atomically, and when each was last started.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _checking = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _lastCheckStarted = new();

    /// <summary>Shortest time between the starts of two manual checks of the same peer: a person double-clicking, or a script, does not turn this node into a flood.</summary>
    public TimeSpan CheckMinGap { get; set; } = TimeSpan.FromSeconds(5);

    // What each peer that failed said last, kept until it succeeds: a peer in a pause (unreachable, closed) is not asked in the cycles that follow,
    // and those cycles must still say that it is not served (BMB-307), not report a clean "applied 0".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PeerSyncFailure> _lastFailure = new();
    private bool _disposed;

    /// <summary>
    /// How long a blind node leaves a peer alone after it answered 401 or 403 (BMB-309): the peer is closed to this node (a reverse proxy
    /// with an allow-list, a revoked key), and asking every minute only fills the log. One retry an hour finds out when that has changed.
    /// </summary>
    public TimeSpan ClosedRetry { get; set; } = ClosedPeers.DefaultRetry;

    private readonly ClosedPeers _closed = new();

    // Blind nodes whose owner's limit stops them (503 + X-BMB-Paused): asked again when they said, never later than an hour, no back-off, one line per episode.
    private readonly PausedPeers _pausedPeers = new();

    // The pause barrier: a flow that is about to delete or replace this node's tables holds the
    // loop off until it is done. See PauseAsync.
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private volatile bool _paused;

    /// <summary>True while a wipe or a reseed owns the mesh state and the loop is held off.</summary>
    public bool IsPaused => _paused;

    /// <summary>The blind-node peers with an open "closed to me" episode (what the loop remembers about doors that said 401/403); a test reads it.</summary>
    internal int ClosedEpisodes => _closed.Count;

    /// <summary>The blind-node peers with an open "paused by its owner" episode; a test reads it.</summary>
    internal int PausedEpisodes => _pausedPeers.Count;

    /// <summary>The shortest wait after a paused answer (a test shortens it to honour a one-second <c>Retry-After</c>).</summary>
    internal TimeSpan PausedMinRetry { get => _pausedPeers.MinWait; set => _pausedPeers.MinWait = value; }

    /// <summary>The clock of the pause bookkeeping (when a paused peer is asked again): a test steps it instead of sleeping.</summary>
    internal Func<DateTime> PauseClock { get; set; } = static () => DateTime.UtcNow;

    /// <summary>The peers this loop cannot reach now, each with the start of its failure streak (<see cref="UnreachablePeers.FailingSince"/>).</summary>
    public IReadOnlyDictionary<Guid, DateTime> UnreachableSince() => _unreachable.FailingSince();

    /// <summary>
    /// Holds the sync loop off and waits out the cycle already running, if any: the caller owns this
    /// node's mesh state until the returned handle is disposed.
    ///
    /// <para>The reseed cutover and "disconnect and wipe" delete or replace the very tables a pull
    /// applies into. Maintenance mode and the event-write gate do not stop this loop: a cycle that
    /// fetches a page and writes its sync position (SyncClient's own commit, outside the gate) either
    /// fails the wipe's own verification or lands after the wipe committed and resurrects mesh state
    /// on a node the operator just disconnected (review release-a2 agy#4, sec#6, spec#3). Taking
    /// <see cref="_syncLock"/> is the drain — a cycle that is already applying finishes, and this
    /// method returns only when it has.</para>
    /// </summary>
    public async Task<IDisposable> PauseAsync(CancellationToken ct = default)
    {
        await _pauseGate.WaitAsync(ct);
        _paused = true;
        try
        {
            await _syncLock.WaitAsync(ct);
            _syncLock.Release();
        }
        catch
        {
            _paused = false;
            _pauseGate.Release();
            throw;
        }
        return new Paused(this);
    }

    private sealed class Paused(SyncScheduler scheduler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            scheduler._paused = false;
            scheduler._pauseGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncScheduler started, interval {Interval}", Interval);

        // A sealed node whose database is closed has nothing to repair or sync until it is unlocked (docs/design/step-b-spec.md 5.5).
        await scopeFactory.WaitUsableAsync(stoppingToken);

        // Before anything is served or pushed from the log: hosts without the API startup (mobile)
        // run the repair here. See StoredEventRepair; a second run elsewhere finds nothing.
        try { await StoredEventRepair.RunAsync(scopeFactory, logger); }
        catch (Exception ex) { logger.LogError(ex, "Stored event repair failed; retried at the next start"); }

        // First sync after a short delay on startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var cycleStart = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await scopeFactory.WaitUsableAsync(stoppingToken);

            // A wipe or a reseed owns the tables right now: skip the whole cycle, the repair scrub
            // below included — it writes event rows like any applier.
            if (_paused)
            {
                await syncTrigger.WaitAsync(Interval, stoppingToken);
                continue;
            }

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
            catch (VaultSealedException)
            {
                // The database closed in the middle of the cycle (a lock): nothing failed, the next iteration waits for the unlock.
                logger.LogDebug("The sync cycle stopped: the database was closed");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SyncScheduler cycle failed, will retry next interval");
                // Still signal completion (with the error) so heartbeat subscribers can tell a
                // failed-but-alive loop apart from a loop that stopped firing entirely.
                // The heartbeat reaches screens and logs of other devices: what kind of failure it was, from the closed set (PeerContactLog.Describe), never
                // the exception's own text (an address, a path, a remote answer). The exception itself is in this node's log above.
                SyncCycleCompleted?.Invoke(this, new SyncCycleResult(0, PeerContactLog.Describe(ex)));
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

        // Who is on the whitelist now, at which address: what was learned about a peer that is gone or moved is forgotten (BMB-313).
        ReconcilePeers(nodes, scope.ServiceProvider.GetService<PeerContactLog>());

        // Only sync with nodes that have an API address configured
        var remoteNodes = nodes.Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();
        snapshotRequiredState?.RetainOnly(remoteNodes.Select(n => n.NodeId).ToHashSet());
        var current = remoteNodes.Select(n => n.NodeId).ToHashSet();
        foreach (var gone in _lastFailure.Keys.Where(id => !current.Contains(id)).ToList()) _lastFailure.TryRemove(gone, out _);
        if (remoteNodes.Count == 0) return new SyncCycleResult(0);
        var failures = new List<PeerSyncFailure>();

        using var http = httpClientFactory.CreateClient(HttpClientName);
        var reseeder = scope.ServiceProvider.GetService<IBlindPeerReseeder>();
        // When each peer last answered a call of this node, and why not (BMB-313): the header status and the Admin pages read it.
        var contacts = scope.ServiceProvider.GetService<PeerContactLog>();

        // A blind node is, in the usual setup, called by its peers; it calls only where nobody can call it (a hub it was paired from, ADR 0007),
        // and a peer that is closed to it (401/403) is left alone for a while instead of being asked, and warned about, every minute (BMB-309).
        // A full node's cycle is what it always was.
        var blind = scope.ServiceProvider.GetService<INodeRole>() is { IsBlind: true };

        foreach (var node in remoteNodes)
        {
            if (ct.IsCancellationRequested) break;
            if (_unreachable.ShouldSkip(node.NodeId, DateTime.UtcNow))
            {
                if (_lastFailure.TryGetValue(node.NodeId, out var asked)) failures.Add(asked);
                continue;
            }
            if (blind && _closed.ShouldSkip(node.NodeId, DateTime.UtcNow, PeerContactLog.EndpointKey(node.ApiAddress)))
            {
                if (_lastFailure.TryGetValue(node.NodeId, out var asked)) failures.Add(asked);
                continue;
            }
            // A peer whose owner's limit stops it is asked again when it said (an hour at most), and meanwhile is not a failure of this cycle: the Blind nodes page says why.
            if (_pausedPeers.ShouldSkip(node.NodeId, PauseClock(), PeerContactLog.EndpointKey(node.ApiAddress)))
                continue;

            Exception? failure = null;
            try
            {
                totalApplied += await syncClient.SyncWithPeerAsync(http, node.ApiAddress!, node.NodeId, ct);
                snapshotRequiredState?.Clear(node.NodeId);
                contacts?.NoteOutboundOk(node.NodeId, DateTime.UtcNow, node.ApiAddress);
                _lastFailure.TryRemove(node.NodeId, out _);
                if (blind && _closed.NoteOpen(node.NodeId))
                    logger.LogInformation("{NodeId} ({Address}) answers this blind node again", node.NodeId, node.ApiAddress);
                EndPausedEpisode(node, "a session went through");
                if (_unreachable.NoteSuccess(node.NodeId) is var failedBefore and > 0)
                    logger.LogInformation("{NodeId} ({Address}) is reachable again after {Failures} failed attempts",
                        node.NodeId, node.ApiAddress, failedBefore);
            }
            catch (PushGapException ex)
            {
                // The PEER is behind our compaction, not us: nothing to flag locally. A blind peer
                // is reseeded below; a full one has to rejoin.
                failure = ex;
                NoteFailed(failures, node, ex);
                contacts?.NoteOutboundOk(node.NodeId, DateTime.UtcNow, node.ApiAddress);   // it answered; what is wrong is its data, not its door
                if (blind) EndClosedEpisode(node, "it answered; its log did not meet this node's");
                logger.LogWarning("{NodeId} ({Address}) missed events our compaction removed (pushed up to {Pushed}, cp={Cp})",
                    node.NodeId, node.ApiAddress, ex.PushedUpTo, ex.LastCompactionCp);
            }
            catch (SnapshotRequiredException ex) when (BlindNodeId.IsBlind(node.NodeId))
            {
                // A blind node refused our pull, so it is the blind node's log that starts above our position: this node
                // is not out of sync, and a wipe of it would cure nothing. SyncWithPeerAsync adopts the blind node's
                // checkpoint itself, so this is what is left when even that did not help (a checkpoint that moved again).
                failure = ex;
                NoteFailed(failures, node, ex);
                contacts?.NoteOutboundOk(node.NodeId, DateTime.UtcNow, node.ApiAddress);
                EndClosedEpisode(node, "it answered; its log starts above this node's position");
                logger.LogCritical(
                    "Blind node {NodeId} ({Url}) refused our pull: its log starts above our position (its checkpoint cp={Cp}, head={Head}). " +
                    "This node is not out of sync and is not to be wiped; reseed the blind node from the Blind nodes page.",
                    node.NodeId, ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq);
            }
            catch (SnapshotRequiredException ex)
            {
                failure = ex;
                NoteFailed(failures, node, ex);
                contacts?.NoteOutboundOk(node.NodeId, DateTime.UtcNow, node.ApiAddress);
                if (blind) EndClosedEpisode(node, "it answered; it has compacted past this node");
                logger.LogCritical(
                    "Node is out-of-sync with {Url}: compacted past us (cp={Cp}, head={Head}). Manual wipe & rejoin required.",
                    ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq);
                snapshotRequiredState?.Set(ex, node.NodeId);
            }
            catch (PeerIncompatibleException ex)
            {
                // It answered, and its software is too old to sync with: a failure of the session (the phone's heartbeat and "Sync now" say it), left alone with
                // a growing pause like a wrong address - it does not become compatible in a minute - and asked at once again when it is changed or checked.
                failure = ex;
                NoteFailed(failures, node, ex);
                contacts?.NoteOutboundFailure(node.NodeId, DateTime.UtcNow, PeerContactLog.Describe(ex), node.ApiAddress);
                if (blind) EndClosedEpisode(node, "it answered; it runs an older version");
                NoteUnreachable(node, ex);
                continue;
            }
            catch (PeerPausedException ex)
            {
                // It answered, and said its owner's limit stops it (503 + X-BMB-Paused): its door is open, so this is contact, not a failure and not a streak; the push
                // waits for the time it named. No reseed either: nothing is wrong with what it holds.
                _lastFailure.TryRemove(node.NodeId, out _);
                _unreachable.NoteSuccess(node.NodeId);
                if (blind) EndClosedEpisode(node, ex.Reason == PeerPauseReport.Busy ? "it answered, busy with other transfers" : "it answered, paused by its owner");
                NotePaused(node, ex, contacts);
                continue;
            }
            catch (Exception ex) when (blind && IsClosedToMe(ex))
            {
                failure = ex;
                NoteFailed(failures, node, ex);
                contacts?.NoteOutboundFailure(node.NodeId, DateTime.UtcNow, PeerContactLog.Describe(ex), node.ApiAddress);
                NoteClosed(node, ex);
                continue;
            }
            catch (Exception ex) when (IsUnreachable(ex, ct))
            {
                failure = ex;
                NoteFailed(failures, node, ex);
                contacts?.NoteOutboundFailure(node.NodeId, DateTime.UtcNow, PeerContactLog.Describe(ex), node.ApiAddress);
                // A 404 or a page that is not an identity IS an answer, and not a refusal: the door of a peer that had closed it is open (what stands behind it is not a sync
                // server, which is its own reason). Only a failure with no answer at all (no connection, a timeout) keeps the episode.
                if (blind && ex is NoSyncServerException) EndClosedEpisode(node, "it answered, but it is not a sync server");
                NoteUnreachable(node, ex);
                continue;
            }
            catch (Exception ex)
            {
                failure = ex;
                if (!ct.IsCancellationRequested)
                {
                    NoteFailed(failures, node, ex);
                    contacts?.NoteOutboundFailure(node.NodeId, DateTime.UtcNow, PeerContactLog.Describe(ex), node.ApiAddress);
                    // It answered with something that is not a refusal (a 404, a 500, a 502 of its proxy): the door is not what stops this node any more.
                    if (blind && ex is HttpRequestException { StatusCode: { } answered }) EndClosedEpisode(node, $"it answered {(int)answered}");
                    if (ex is HttpRequestException { StatusCode: not null }) EndPausedEpisode(node, "it answered with something that is not a pause");
                }
                logger.LogWarning(ex, "Error synchronizing with {NodeId} ({Address})",
                    node.NodeId, node.ApiAddress);
            }

            if (reseeder != null && BlindNodeId.IsBlind(node.NodeId))
            {
                try { await reseeder.AfterSyncAsync(node, http, failure, ct); }
                catch (PeerPausedException ex) when (!ct.IsCancellationRequested)
                {
                    // The blind node took the call and said its owner's limit stops the seed (503 + X-BMB-Paused): a pause, held for the time it named - not a failed reseed.
                    NotePaused(node, ex, contacts);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Reseeding blind node {NodeId} ({Address}) failed", node.NodeId, node.ApiAddress);
                }
            }
        }

        return new SyncCycleResult(totalApplied, null, failures);
    }

    /// <summary>Remembers why this peer was not served, for this cycle's result and for the cycles in which it is left alone.</summary>
    private void NoteFailed(List<PeerSyncFailure> failures, WhitelistEntry node, Exception ex)
    {
        var failed = PeerSyncFailure.From(node, ex);
        _lastFailure[node.NodeId] = failed;
        failures.Add(failed);
    }

    /// <summary>The answer of <see cref="CheckPeerNowAsync"/>.</summary>
    /// <param name="Called">This node called the peer now (false: no address, or the peer is not on the whitelist).</param>
    /// <param name="Answered">The session reached the peer and it answered (true even when its data is the problem: <paramref name="Reason"/> says so).</param>
    /// <param name="Busy">A cycle is running or the loop is held off (a wipe): nothing was asked, try again in a moment.</param>
    /// <param name="Applied">Events applied from the peer in this session.</param>
    /// <param name="Reason">Why the peer did not answer, or what is wrong with it though it did.</param>
    public sealed record PeerCheck(bool Called, bool Answered, bool Busy, int Applied, string? Reason);

    /// <summary>
    /// One session with one peer, now, outside the minute's rhythm (BMB-313: a double-click on a node of the sync status). It takes the lock of the loop,
    /// so it never overlaps a cycle, runs the same session a cycle would, and leaves the same traces (<see cref="PeerContactLog"/>, the unreachable and
    /// closed memories end when it succeeds). A peer in a pause of the loop is asked anyway: the person asked.
    /// </summary>
    public async Task<PeerCheck> CheckPeerNowAsync(Guid nodeId, CancellationToken ct = default)
    {
        var busy = new PeerCheck(false, false, true, 0, null);
        if (_paused) return busy;

        // One check of one peer at a time, decided here, atomically, before anything is awaited: a second request for the same peer (another tab, another
        // person, a script) is answered AT ONCE with "busy" and queues nothing behind the lock of the loop. And one start per CheckMinGap per peer.
        if (!_checking.TryAdd(nodeId, 0)) return busy;
        try
        {
            var now = DateTime.UtcNow;
            if (_lastCheckStarted.TryGetValue(nodeId, out var last) && now - last < CheckMinGap) return busy;
            _lastCheckStarted[nodeId] = now;

            // The loop's own lock: a check never overlaps a cycle (a cycle that is running finishes first), so scheduled work and a check share one gate.
            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(20), ct)) return busy;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var contacts = scope.ServiceProvider.GetService<PeerContactLog>();
                var active = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
                ReconcilePeers(active, contacts);   // an address that changed since the last cycle is noticed now, before it is called
                var node = active.FirstOrDefault(n => n.NodeId == nodeId);
                if (node is null || string.IsNullOrEmpty(node.ApiAddress))
                    return new PeerCheck(false, false, false, 0, null);

                var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();
                using var http = httpClientFactory.CreateClient(HttpClientName);
                try
                {
                    var applied = await syncClient.SyncWithPeerAsync(http, node.ApiAddress!, node.NodeId, ct);
                    snapshotRequiredState?.Clear(node.NodeId);
                    contacts?.NoteOutboundOk(node.NodeId, DateTime.UtcNow, node.ApiAddress);
                    _unreachable.NoteSuccess(node.NodeId);
                    _closed.NoteOpen(node.NodeId);
                    EndPausedEpisode(node, "a check went through");
                    _lastFailure.TryRemove(node.NodeId, out _);
                    return new PeerCheck(true, true, false, applied, null);
                }
                catch (PeerPausedException ex)
                {
                    // It answered: paused by its owner. Not a failure of the check ("answered" is true) and the reason is the pause, in this node's words.
                    _lastFailure.TryRemove(node.NodeId, out _);
                    _unreachable.NoteSuccess(node.NodeId);
                    NotePaused(node, ex, contacts);
                    return new PeerCheck(true, true, false, 0, PeerContactLog.Describe(ex));
                }
                catch (Exception ex) when (ex is PushGapException or SnapshotRequiredException)
                {
                    // It answered; its log and ours do not meet. The cycle's own handling (the alert, the blind page) is the place for the cure.
                    contacts?.NoteOutboundOk(node.NodeId, DateTime.UtcNow, node.ApiAddress);
                    return new PeerCheck(true, true, false, 0, PeerContactLog.Describe(ex));
                }
                catch (PeerIncompatibleException ex)
                {
                    // It answered, and its software is too old: not a session that went well (the check does not say "online"), and not "no answer" either.
                    var reason = PeerContactLog.Describe(ex);
                    contacts?.NoteOutboundFailure(node.NodeId, DateTime.UtcNow, reason, node.ApiAddress);
                    _lastFailure[node.NodeId] = PeerSyncFailure.From(node, ex);
                    return new PeerCheck(true, true, false, 0, reason);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var reason = PeerContactLog.Describe(ex);
                    contacts?.NoteOutboundFailure(node.NodeId, DateTime.UtcNow, reason, node.ApiAddress);
                    _lastFailure[node.NodeId] = PeerSyncFailure.From(node, ex);
                    logger.LogWarning("The check of {NodeId} ({Address}) failed: {Type}", node.NodeId, node.ApiAddress, ex.GetType().Name);
                    return new PeerCheck(true, false, false, 0, reason);
                }
            }
            finally
            {
                _syncLock.Release();
            }
        }
        finally
        {
            _checking.TryRemove(nodeId, out _);
        }
    }

    /// <summary>
    /// What this loop knows about a peer is its own to forget: called when a peer was revoked, replaced or given another address (the writers of the whitelist
    /// call it; the loop also notices by itself at its next cycle, <see cref="ReconcilePeers"/>). The pause of an unreachable peer, the closed window, the
    /// failure reason and what the contact record learned by calling it go; the next cycle is signalled, so the new address is tried at once.
    /// </summary>
    public void PeerChanged(Guid nodeId)
    {
        using var scope = scopeFactory.CreateScope();
        ForgetPeerState(nodeId, scope.ServiceProvider.GetService<PeerContactLog>());
        _endpoints.TryRemove(nodeId, out _);   // the next cycle records the address it finds
        syncTrigger.Signal();
    }

    private void ForgetPeerState(Guid nodeId, PeerContactLog? contacts)
    {
        _unreachable.Forget(nodeId);
        _closed.Forget(nodeId);
        _pausedPeers.Forget(nodeId);
        _lastFailure.TryRemove(nodeId, out _);
        _lastCheckStarted.TryRemove(nodeId, out _);
        contacts?.ForgetOutbound(nodeId);
    }

    /// <summary>
    /// Compares the whitelist with what this loop remembers: a peer that has another address than at the last look starts again (nothing learned at the old
    /// one counts for the new one), a peer that is gone is forgotten, and the contact log keeps only the peers on the whitelist (and none that nobody has touched
    /// for <see cref="PeerContactLog.Retention"/>), so that no memory grows with the nodes a vault ever met.
    /// </summary>
    internal void ReconcilePeers(IReadOnlyList<WhitelistEntry> active, PeerContactLog? contacts)
    {
        var addressed = new Dictionary<Guid, string>();
        foreach (var node in active)
            if (PeerContactLog.EndpointKey(node.ApiAddress) is { } key) addressed[node.NodeId] = key;

        foreach (var (id, key) in addressed)
        {
            if (_endpoints.TryGetValue(id, out var seen) && !string.Equals(seen, key, StringComparison.Ordinal))
                ForgetPeerState(id, contacts);
            _endpoints[id] = key;
        }
        foreach (var gone in _endpoints.Keys.Where(id => !addressed.ContainsKey(id)).ToList())
        {
            _endpoints.TryRemove(gone, out _);
            ForgetPeerState(gone, contacts);
        }
        var activeIds = active.Select(n => n.NodeId).ToHashSet();
        foreach (var id in _lastCheckStarted.Keys.Where(id => !activeIds.Contains(id)).ToList()) _lastCheckStarted.TryRemove(id, out _);
        _closed.Retain(activeIds);   // a revoked or deleted node leaves no closed episode behind
        _pausedPeers.Retain(activeIds);   // nor a paused one
        contacts?.Reconcile(activeIds, DateTime.UtcNow);
    }

    /// <summary>Could not talk to the peer at all — as opposed to talking and being refused.</summary>
    internal static bool IsUnreachable(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        NoSyncServerException => true,   // a wrong address stays wrong: one warning per streak and growing pauses, not a warning every minute
        TaskCanceledException when !ct.IsCancellationRequested => true, // the HttpClient timeout
        _ => false
    };

    /// <summary>The peer answered, and said no: its proxy does not let this node's address in, or it no longer knows this node.</summary>
    internal static bool IsClosedToMe(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };

    /// <summary>One short line when a peer closes its door on a blind node; the retries after it, while it stays closed, are debug.</summary>
    private void NoteClosed(WhitelistEntry node, Exception ex)
    {
        var status = ((HttpRequestException)ex).StatusCode;
        if (_closed.NoteClosed(node.NodeId, DateTime.UtcNow, ClosedRetry, PeerContactLog.EndpointKey(node.ApiAddress)))
            logger.LogWarning("{NodeId} ({Address}) answers {Status}: it is closed to this blind node; asking again every {Retry}",
                node.NodeId, node.ApiAddress, (int?)status, ClosedRetry);
        else
            logger.LogDebug("{NodeId} ({Address}) still answers {Status}", node.NodeId, node.ApiAddress, (int?)status);
    }

    /// <summary>The one line that says a peer that had closed its door is open again, written when it gives an answer that is not a refusal (see <see cref="ClosedPeers"/>).</summary>
    private void EndClosedEpisode(WhitelistEntry node, string how)
    {
        if (_closed.NoteOpen(node.NodeId))
            logger.LogInformation("{NodeId} ({Address}) no longer answers 401/403 ({How}): its door is open to this blind node again", node.NodeId, node.ApiAddress, how);
    }

    /// <summary>
    /// A blind node said its owner's limit stops it: the pause is remembered (asked again when it said, never later than an hour), the contact log shows it in the hub's own
    /// words, and the episode's one line is information, not a warning: nothing is wrong, the node was told to wait (see <see cref="PausedPeers"/>). A node that said
    /// <see cref="PeerPauseReport.Busy"/> is not paused by its owner: it is busy with other transfers and is asked again in about a minute, and the log says that.
    /// </summary>
    private void NotePaused(WhitelistEntry node, PeerPausedException ex, PeerContactLog? contacts)
    {
        var now = PauseClock();
        // A traffic limit lifts at the node's next local midnight and the node says how long that is; a storage limit lifts when its owner changes it: no end to show.
        contacts?.NotePausedByNode(node.NodeId, now, ex.Reason, ex.Reason == PeerPauseReport.Traffic ? now + ex.RetryAfter : null, node.ApiAddress);
        var busy = ex.Reason == PeerPauseReport.Busy;
        if (_pausedPeers.NotePaused(node.NodeId, now, ex.RetryAfter, ex.Reason, PeerContactLog.EndpointKey(node.ApiAddress)))
        {
            if (busy)
                logger.LogInformation("{NodeId} ({Address}) is busy with other transfers right now; nothing is wrong with it and nothing is sent until it can take more, asking again in about a minute",
                    node.NodeId, node.ApiAddress);
            else
                logger.LogInformation("{NodeId} ({Address}) is paused by its owner ({Reason}); nothing is wrong with it and nothing is sent until it can take more, asking again at most every {Retry}",
                    node.NodeId, node.ApiAddress, ex.Reason, PausedPeers.MaxRetry);
        }
        else if (busy)
            logger.LogDebug("{NodeId} ({Address}) is still busy with other transfers", node.NodeId, node.ApiAddress);
        else
            logger.LogDebug("{NodeId} ({Address}) is still paused by its owner ({Reason})", node.NodeId, node.ApiAddress, ex.Reason);
    }

    /// <summary>The one line that says a peer that was paused by its owner takes data again, written when it gives an answer that is not a pause (see <see cref="PausedPeers"/>).</summary>
    private void EndPausedEpisode(WhitelistEntry node, string how)
    {
        var busy = _pausedPeers.ReasonOf(node.NodeId) == PeerPauseReport.Busy;
        if (_pausedPeers.NoteOpen(node.NodeId))
        {
            if (busy)
                logger.LogInformation("{NodeId} ({Address}) is no longer busy with other transfers ({How})", node.NodeId, node.ApiAddress, how);
            else
                logger.LogInformation("{NodeId} ({Address}) is no longer paused by its owner ({How})", node.NodeId, node.ApiAddress, how);
        }
    }

    /// <summary>
    /// Only the first failure of a streak is a warning, the rest go to debug — so an absent phone
    /// does not fill the log with the same line every minute.
    /// </summary>
    private void NoteUnreachable(WhitelistEntry node, Exception ex)
    {
        var (failures, pause) = _unreachable.NoteFailure(node.NodeId, DateTime.UtcNow, Interval);
        if (failures == 1 && ex is PeerIncompatibleException incompatible)
            logger.LogWarning("{NodeId} ({Address}) runs sync protocol {Protocol}, which this node no longer syncs with: it has to be upgraded; asking again with a growing pause, up to {Max}",
                node.NodeId, node.ApiAddress, incompatible.RemoteProtocol, UnreachablePeers.MaxPause);
        else if (failures == 1)
            // The kind of failure from the closed set, and the type: what the exception says (the address dialled, the other side's words, a proxy's page) stays out of the log.
            logger.LogWarning("{NodeId} ({Address}) is unreachable ({Reason}, {ErrorType}); retrying with a growing pause, up to {Max}",
                node.NodeId, node.ApiAddress, PeerContactLog.Describe(ex), ex.GetType().Name, UnreachablePeers.MaxPause);
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

/// <param name="TotalApplied">Events applied from all peers.</param>
/// <param name="Error">The cycle itself failed (nothing was tried).</param>
/// <param name="PeerFailures">The peers this cycle could not finish with, each with the reason; empty when every peer was reached.</param>
public record SyncCycleResult(int TotalApplied, string? Error = null, IReadOnlyList<PeerSyncFailure>? PeerFailures = null)
{
    /// <summary>
    /// Why this cycle was not clean: its own error, or what each peer that failed said (BMB-307). Null only when every peer with an address was reached.
    /// A status that says "ok" is for that case alone: "ok (applied 0)" next to a peer that answered 404 hid a wrong address for weeks.
    /// </summary>
    public string? Problem => Error ?? (PeerFailures is { Count: > 0 } failures ? string.Join("; ", failures) : null);
}

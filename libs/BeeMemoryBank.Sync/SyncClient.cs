using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Performs bidirectional synchronization with a single remote node over HTTP.
/// Pull: downloads events from the remote node and applies them.
/// Push: sends local events to the remote node.
/// </summary>
public class SyncClient(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ISyncPositionRepository syncPositionRepo,
    ISyncPushPositionRepository pushPositionRepo,
    EventApplier eventApplier,
    INodeAuthSigner authSigner,
    ILogger<SyncClient> logger,
    PeerNewerProtocolState peerNewerProtocolState,
    ISyncQuarantineRepository quarantineRepo,
    IBlobRepository blobRepo,
    IRestoreRetrier? restoreRetrier = null,
    BlindState? blindState = null,
    IRemoteSentinelVerifier? sentinelVerifier = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A full node's sync with one whitelisted peer (the scheduler's cycle, and the reseed's pull): <see cref="SyncWithAsync"/>,
    /// plus one rule for a BLIND peer. A blind node is seeded from this node's own package, and its log then starts at
    /// the checkpoint the seed left (its <c>last_compaction_cp</c>): everything at or below it is this node's own content.
    /// A node that has NO pull position for it (a used volume paired again after a re-key cleared the positions) would be
    /// answered 410 SEQUENCE_TOO_OLD on every cycle, the pull would throw before the push, and nothing would ever reach
    /// the blind node. So on that 410 the pull position becomes the peer's checkpoint and the sync is retried once.
    /// <para>The checkpoint is the peer's own word, so it is adopted only when ALL of these hold; otherwise the 410
    /// propagates, and the scheduler reports it as the blind node's refusal:
    /// (a) the body's code is <c>SEQUENCE_TOO_OLD</c>, not just any 410; (b) this node holds no pull position for the peer
    /// (a cursor that exists is never advanced by a 410, which is also what makes an adoption happen once per pairing: it
    /// leaves the cursor behind, so a second 410 is a refusal); (c) the peer is blind; (d) the checkpoint is above zero and
    /// not above the head the same body reports (a body without a head is refused). A blind node's log is empty right
    /// after a seed while its checkpoint is the old head, so it reports head 0: an empty log has no head to check the
    /// checkpoint against and is taken at its word (see the last paragraph).</para>
    /// <para>The position adopted is the blind node's OWN checkpoint, in its own sequence space, never the package's
    /// (which is in this node's space): using that would skip what the phones push to the blind node next. The blind
    /// side's counter is not touched: other peers hold positions in that space.</para>
    /// <para>Not for a phone: the mobile hosts call <see cref="SyncWithAsync"/>, where a peer that pulled below a blind
    /// node's checkpoint still gets its 410. A 410 from a peer that is not blind is a real wipe-and-rejoin case and stays
    /// one. A <see cref="PushGapException"/> is the other direction (the peer is behind us) and is never adopted.</para>
    /// <para>The accepted limit: a blind peer can still claim a checkpoint inside its own log (or, with an empty log, any
    /// checkpoint), which equals withholding the events below it, and a peer that withholds events can do that anyway. What
    /// it withholds is not lost: the phones that pushed those events to it also sync with this node directly. Nothing here
    /// can be proven from the peer's numbers alone; what is checked is that a 410 cannot move a cursor that exists, cannot
    /// carry a claim its own body contradicts, and cannot do it more than once per pairing.</para>
    /// </summary>
    public async Task<int> SyncWithPeerAsync(
        HttpClient http, string remoteApiBase, Guid peerNodeId, CancellationToken ct = default)
    {
        try
        {
            return await SyncWithAsync(http, remoteApiBase, peerNodeId, ct);
        }
        catch (SnapshotRequiredException ex) when (ex is not PushGapException && BlindNodeId.IsBlind(peerNodeId))
        {
            if (await WhyNotToAdoptAsync(ex, peerNodeId) is { } why)
            {
                logger.LogWarning("Not taking the checkpoint of blind node {NodeId} ({Url}) as the pull position: {Why}.",
                    peerNodeId, ex.RemoteUrl, why);
                throw;
            }
            // The rules above read the position; the write must not trust that read. Two syncs with the same blind peer
            // can overlap (the scheduler's cycle and a reseed's pull from the Blind nodes page do not share a lock), and
            // with an upsert both adopted, the second moving the cursor the first had just set. TryAdoptAsync only ever
            // creates the row: one caller adopts, the other finds a cursor and simply syncs from it (where a further 410
            // is a refusal, as for any cursor that exists).
            if (!await syncPositionRepo.TryAdoptAsync(peerNodeId, ex.LastCompactionCp))
                return await SyncWithAsync(http, remoteApiBase, peerNodeId, ct);
            logger.LogWarning(
                "Blind node {NodeId} ({Url}) starts its log at checkpoint {Cp} (its head is {Head}); this node held no pull " +
                "position for it. Everything at or below the checkpoint came from this node's own seed, so the pull position " +
                "is now {Cp}.",
                peerNodeId, ex.RemoteUrl, ex.LastCompactionCp, ex.CurrentHeadSeq, ex.LastCompactionCp);
            await RecordAdoptionAsync(peerNodeId, ex.LastCompactionCp);
            return await SyncWithAsync(http, remoteApiBase, peerNodeId, ct);
        }
    }

    /// <summary>Why a blind peer's 410 must not become a pull position, or null when it may (see <see cref="SyncWithPeerAsync"/>).</summary>
    private async Task<string?> WhyNotToAdoptAsync(SnapshotRequiredException ex, Guid peerNodeId)
    {
        if (!string.Equals(ex.ErrorCode, SnapshotRequiredException.SequenceTooOldCode, StringComparison.Ordinal))
            return $"its 410 does not carry the {SnapshotRequiredException.SequenceTooOldCode} code (it says \"{ex.ErrorCode ?? "nothing"}\")";
        if (await syncPositionRepo.GetAsync(peerNodeId) is { } held)
            return $"this node already holds a pull position for it ({held.LastSequenceNum}), and a 410 never moves a cursor that exists";
        if (ex.LastCompactionCp <= 0)
            return $"its checkpoint ({ex.LastCompactionCp}) is not above zero";
        if (!ex.HeadReported || ex.CurrentHeadSeq < 0)
            return "it does not say what its head is";
        if (ex.CurrentHeadSeq > 0 && ex.LastCompactionCp > ex.CurrentHeadSeq)
            return $"its checkpoint ({ex.LastCompactionCp}) is above the head ({ex.CurrentHeadSeq}) it reports for the same log";
        return null;
    }

    /// <summary>What the Blind nodes page shows as "adopted checkpoint N". A failure to note it must not fail the sync.</summary>
    private async Task RecordAdoptionAsync(Guid peerNodeId, long checkpoint)
    {
        if (blindState is null) return;
        try { await blindState.SetAdoptedCheckpointAsync(peerNodeId, checkpoint); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not note the adopted checkpoint of blind node {NodeId}", peerNodeId); }
    }

    /// <summary>
    /// Synchronizes with a remote node. Returns the number of new events applied locally.
    /// </summary>
    /// <param name="expectedPeerNodeId">
    /// The whitelist entry's NodeId for the peer being dialed — the audience anchor for the
    /// challenge-relay protection. It is a required parameter rather than something resolved in
    /// here precisely because it must come from a source the peer does not control: every caller
    /// (SyncScheduler, and the mobile InitialSyncPage/SyncWorker/SyncStatusService) is already
    /// iterating tbl_whitelist rows and has it in hand. Do not resolve it by matching
    /// remoteApiBase against tbl_whitelist.api_address: an address in a different shape resolves
    /// to "nothing pinned", the only safe answer to which is refusing, so a string mismatch would
    /// become a sync outage. Passing the id explicitly keeps string comparison out of the trust path.
    /// </param>
    public async Task<int> SyncWithAsync(
        HttpClient http, string remoteApiBase, Guid expectedPeerNodeId, CancellationToken ct = default)
    {
        // Defensive: every request below is built as $"{remoteApiBase}/api/sync/...", so a base
        // that ends in a slash produces a double slash and a 404 from ASP.NET routing. Addresses
        // are normalized on ingest (JoinEndpoints, WhitelistEndpoints), but a row predating that,
        // or a caller assembling an address by hand, would otherwise wedge sync with this peer.
        remoteApiBase = remoteApiBase.TrimEnd('/');

        // Belt-and-suspenders for stuck restores: in addition to the unlock-time sweep in
        // SessionService, retry stuck restore events at the start of every sync cycle.
        // Catches the case where the user stays unlocked but a transient failure (network,
        // disk) left a restore in Pending/Downloading. Cheap — no-op if nothing stuck.
        if (restoreRetrier != null)
        {
            try { await restoreRetrier.RetryPendingRestoresAsync(); }
            catch (Exception ex) { logger.LogWarning(ex, "Pre-sync restore retry sweep failed"); }
        }

        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Local node is not initialized.");

        // 0. Verify DEK compatibility via sentinel - a check only a node that holds the master DEK can make
        //    (IRemoteSentinelVerifier is registered by such nodes; a blind node has none and skips it)
        if (sentinelVerifier is not null)
            await sentinelVerifier.VerifyAsync(http, remoteApiBase, ct);

        // 1. Get remote node identity
        var remoteIdentity = await GetRemoteIdentityAsync(http, remoteApiBase, ct);
        logger.LogDebug("Synchronizing with {NodeId} ({Base})", remoteIdentity.NodeId, remoteApiBase);

        // 2. Authentication. The audience anchor for the challenge-relay protection is
        // expectedPeerNodeId — the caller's whitelist entry — never remoteIdentity.NodeId, which
        // is self-declared by step 1's /api/sync/identity call on THIS SAME CONNECTION and so is
        // fully controlled by a malicious/compromised peer (or a LAN MITM; plain-HTTP peers are
        // realistic given mDNS discovery). Anchoring on the self-report would let such a peer
        // declare itself to be whatever third node C it likes, relay a genuine challenge fetched
        // live from C (whose ServerNodeId then "matches" what it just told us), and walk away
        // with a signature bound to C that it can redeem there — the exact relay attack the
        // binding exists to stop, moved one level up.
        if (expectedPeerNodeId != remoteIdentity.NodeId)
        {
            // Fail fast, before ever touching the network for a challenge: the peer at this
            // address claims to be someone OTHER than the whitelist entry we dialed. This alone
            // isn't what stops the relay attack (a peer could report a self-consistent identity
            // here while still relaying a mismatched challenge from elsewhere — that's what
            // PeerAuthenticator's own check, driven by the same anchor, actually stops) but it
            // gives a far clearer diagnosis for the mundane case — a stale/wrong ApiAddress in
            // our own whitelist — than a "challenge audience mismatch" thrown from deep inside
            // the auth handshake.
            throw new InvalidOperationException(
                $"Peer at {remoteApiBase} declares NodeId {remoteIdentity.NodeId}, but we dialed it as " +
                $"{expectedPeerNodeId}. Refusing to sync — this is either a stale/incorrect ApiAddress " +
                "entry in our own whitelist, or a peer impersonation attempt.");
        }

        // Below protocol 3 a peer would accept a blind node's events and seal the DEK for it
        // (SyncProtocolVersion.MinPeer). It must not learn of one from us, and anything we pulled
        // from it could have come from one — so nothing moves either way until it upgrades. It
        // refuses our token request anyway; checking first keeps the log clear about why.
        if (!SyncProtocolVersion.IsCompatiblePeer(remoteIdentity.ProtocolVersion))
        {
            logger.LogWarning("Remote node {NodeId} is on protocol {Remote} < {Min}; not syncing with it until it upgrades.",
                remoteIdentity.NodeId, remoteIdentity.ProtocolVersion, SyncProtocolVersion.MinPeer);
            return 0;
        }

        var token = await AuthenticateAsync(http, remoteApiBase, identity, expectedPeerNodeId, ct);

        int appliedCount = 0;

        if (remoteIdentity.ProtocolVersion > SyncProtocolVersion.Current)
        {
            logger.LogWarning("Remote node {NodeId} has a newer protocol version ({RemoteVersion} > {LocalVersion}). Skipping pull-and-apply.",
                remoteIdentity.NodeId, remoteIdentity.ProtocolVersion, SyncProtocolVersion.Current);
            peerNewerProtocolState.HasNewerProtocol = true;

            // Still tell the peer where we stand. Its compaction refuses to run while any active
            // peer looks far behind, and "far behind" is judged from the position we last
            // reported — a node that stops reporting because it cannot apply newer events would
            // otherwise freeze the peer's event log at its old cursor for as long as it stays on
            // the old protocol. (Protocol-1 builds do not report here, so they still freeze a
            // protocol-2 server's log; nothing on this side can fix that.)
            var stale = await syncPositionRepo.GetAsync(remoteIdentity.NodeId);
            await ReportPositionAsync(http, remoteApiBase, token, stale?.LastSequenceNum ?? 0, ct);
        }
        else
        {
            peerNewerProtocolState.HasNewerProtocol = false;

            // 3. Pull: download new events from the remote node
            var position = await syncPositionRepo.GetAsync(remoteIdentity.NodeId);
            var afterSeq = position?.LastSequenceNum ?? 0;

            var remoteEvents = await PullEventsAsync(http, remoteApiBase, token, afterSeq, ct);
            logger.LogDebug("Received {Count} events from {NodeId}", remoteEvents.Count, remoteIdentity.NodeId);

            // Protocol 2: events name their ciphertext by hash. Fetch every blob this page needs
            // that we do not already hold BEFORE applying anything, so EventApplier never touches
            // the network — a blob still missing after this step fails its event with
            // BlobMissingException and goes through the ordinary retry/quarantine path below.
            await BlobTransport.FetchForAsync(http, remoteApiBase, token, remoteEvents, blobRepo, logger, ct);

            long lastApplied = afterSeq;
            int droppedCount = 0;
            int ownCount = 0;
            foreach (var evt in remoteEvents)
            {
                // Our own event, come back to us (F5). A blind node that was reseeded replays the tail
                // it had received onto the package and re-logs it under the new database's sequences,
                // and we pull that back. While the event is still in our own log the applier's
                // "already applied" shortcut catches it — after a compaction it is not, so it reaches
                // the whitelist lookup, where we are not a peer of ourselves, and is quarantined as
                // deferred. A quarantine row is not cosmetic: the state anchor publishes nothing while
                // one exists, so anchors stop for good.
                //
                // Skipped only when it is PROVEN ours (Codex round 2, security #2): the node id rides
                // on the wire and is not covered by the signature, so "it says our id" is worth
                // nothing on its own. A peer that fabricates an event with our id — and a sequence
                // past the end of what we hold — would otherwise make us walk past the real events
                // behind it, cursor and all, and on a blind node past the blind-authorship invariant
                // too. An unproven one goes through the ordinary apply, where the invariants, the
                // whitelist and the signature run, and the cursor moves only as far as that allows.
                if (evt.NodeId == identity.NodeId && await IsProvenOwnAsync(evt, identity))
                {
                    lastApplied = evt.SequenceNum;
                    ownCount++;
                    continue;
                }

                try
                {
                    var result = await eventApplier.ApplyAsync(evt);
                    if (result == EventApplyResult.SilentlyDropped)
                    {
                        // Permanently dropped — advance cursor so we don't re-fetch this event next cycle.
                        // Replay shield and hard-delete gate are monotone (only get raised, not lowered;
                        // shield is auto-cleared by next RESTORE_NETWORK or manual admin action — neither
                        // makes us "want to retry" the dropped event).
                        lastApplied = evt.SequenceNum;
                        droppedCount++;
                    }
                    else
                    {
                        lastApplied = evt.SequenceNum;
                        appliedCount++;
                    }
                    // Applied cleanly (this cycle, possibly after earlier transient failures) —
                    // forget any failure streak SyncEventQuarantine was tracking for it.
                    await SyncEventQuarantine.ClearFailureAsync(quarantineRepo, evt.EventId);
                }
                catch (Exception ex)
                {
                    // A genuinely broken event (bad signature, whitelist ordering, any other
                    // permanent failure) must not stop the WHOLE page every cycle forever: the
                    // cursor would never move past it. Once the SAME event has exhausted its
                    // budget (SyncEventQuarantine.IsQuarantined — a handful of attempts for a
                    // permanent failure, hours for a deferred one: originator not yet whitelisted,
                    // blob not yet transported, DEK rotation predecessor not yet applied — see
                    // SyncFailureClassifier), treat it as skipped: advance past it and keep going.
                    // A failure still within its budget stops here and is retried from this
                    // position next cycle.
                    var quarantined = await SyncEventQuarantine.RecordFailureAsync(quarantineRepo, evt.EventId, evt.EventType, evt.NodeId, ex);
                    if (quarantined)
                    {
                        logger.LogError(ex,
                            "QUARANTINED event {Seq} ({EventId}, {Type}) — exhausted its retry budget, " +
                            "skipping permanently. See GET /api/sync/quarantine.",
                            evt.SequenceNum, evt.EventId, evt.EventType);
                        lastApplied = evt.SequenceNum;
                        droppedCount++;
                        continue;
                    }

                    logger.LogError(ex, "Failed to apply event {Seq} from remote, stopping sync. Will retry from this position.", evt.SequenceNum);
                    break;
                }
            }

            // Recorded on every successful pull, empty page included (F6): the row's timestamp is what
            // "this node has caught up with this peer" is judged by — the state anchor publishes only
            // when every peer it pulls from was pulled from within StateAnchorScheduler.FreshPull —
            // and in a quiet network nothing ever moves the sequence number, so a row only written
            // when events arrive ages out and no anchor is published again. The sequence number still
            // only moves when something was applied (or skipped): an empty pull says nothing new.
            await syncPositionRepo.UpsertAsync(new SyncPosition
            {
                RemoteNodeId = remoteIdentity.NodeId,
                LastSequenceNum = lastApplied,
                UpdatedAt = DateTime.UtcNow
            });
            if (remoteEvents.Count > 0)
                logger.LogInformation("Pull: applied {Applied}, dropped {Dropped}, own {Own}. Position: {Seq}",
                    appliedCount, droppedCount, ownCount, lastApplied);

            // Always report our current position back to the remote — even when we're fully caught up
            // and there were no new events. Otherwise the remote never learns our position and shows
            // "Waiting for first sync — Never" forever, and compaction thinks we have no active peers.
            await ReportPositionAsync(http, remoteApiBase, token, lastApplied, ct);
        }

        // 4. Push: relay all events to the remote node (excluding its own events)
        //
        // Pushed HTTP requests are bounded by cumulative payload SIZE
        // (SplitIntoByteBoundedBatches), not just event count — a fixed 500-event batch can
        // embed a single ~20MB media_create event (~27MB once base64-encoded), and JsonContent
        // sends a chunked body with no Content-Length, so a server-side Content-Length guard
        // cannot be relied on to catch it. PushChunkWithSplitAsync is the safety net if a chunk is still
        // rejected as too large despite the size-aware split (e.g. a config mismatch between this
        // node's MediaService size limit and the peer's per-request cap): it halves and retries
        // rather than resending the same request forever, quarantining a single event that's still
        // too large alone (see SyncEventQuarantine and GET /api/sync/quarantine).
        const int PushFetchSize = 500;
        const long PushBatchByteTarget = 8 * 1024 * 1024;

        var pushPosition = await pushPositionRepo.GetAsync(remoteIdentity.NodeId);
        long pushAfter = pushPosition?.LastPushedSeq ?? 0;
        int totalApplied = 0, totalSkipped = 0, totalDropped = 0;
        long localMaxSeq = await eventLogRepo.GetMaxSequenceAsync();

        // Gap detector (plan 5.1): relaying "everything after pushAfter" would start at the oldest
        // event still here, above the compaction point, and the peer would never see what lay in
        // between. A peer that also pulls from us gets a 410 for that; a peer we only ever push to
        // — a blind node — would not notice at all.
        //
        // Only for a peer we have pushed to before. With no record at all we do not know what it
        // holds — it may have been seeded or joined from another node's snapshot — and pushing
        // what we have is what it always got.
        if (pushPosition != null
            && await eventLogRepo.GetLastCompactionCpAsync() is { } lastCompactionCp && pushAfter < lastCompactionCp)
            throw new PushGapException(remoteApiBase, remoteIdentity.NodeId, pushAfter, lastCompactionCp, localMaxSeq);

        logger.LogInformation("Push to {Remote}: localMaxSeq={MaxSeq}, pushAfter={After}", remoteIdentity.NodeId, localMaxSeq, pushAfter);
        while (true)
        {
            // PushFetchSize bounds how many candidate rows we pull from the local event log in one
            // page (unrelated to request size — this is just how many rows we consider before
            // deciding whether there's more to fetch after this page).
            var page = await eventLogRepo.GetEventsToRelayAsync(remoteIdentity.NodeId, pushAfter, PushFetchSize);
            logger.LogInformation("Push page to {Remote}: {Count} events", remoteIdentity.NodeId, page.Count);
            if (page.Count == 0) break;

            bool stalled = false;
            foreach (var chunk in SplitIntoByteBoundedBatches(page, PushBatchByteTarget))
            {
                // Blobs first, then the events that reference them — the receiver applies events
                // as they arrive and cannot call back for bytes it lacks (it is usually behind
                // NAT). Idempotent, so a chunk retried next cycle just re-checks and re-ships
                // whatever the peer still reports missing.
                await BlobTransport.ShipForAsync(http, remoteApiBase, token, chunk, blobRepo, logger, ct);

                var (applied, skipped, dropped, lastAppliedSeq) =
                    await PushChunkWithSplitAsync(http, remoteApiBase, token, chunk, ct);
                totalApplied += applied;
                totalSkipped += skipped;
                totalDropped += dropped;

                // Advance the cursor only as far as the remote actually applied. If the remote
                // skipped event N (signature, schema, replay shield, etc.) and applied N+1,
                // advancing to the chunk end (`chunk[^1].SequenceNum`) would jump past N
                // permanently — the remote would never see it again.
                //
                // Three cases:
                //   1. New server → LastAppliedSequence is the end of its contiguous accepted
                //      prefix. Skipped events stay in our outbox until either they get applied on
                //      the remote or admin intervenes.
                //   2. New server, Applied == 0 and Dropped == 0 → LastAppliedSequence is null AND nothing landed.
                //      Don't advance; break to surface the stall via /api/sync/status.
                //   3. Older server (LastAppliedSequence absent in JSON, deserializes to null) but
                //      Applied > 0 → no per-event detail available; treat "got something, no
                //      detail" as "advance to end of chunk", matching that server's semantics.
                if (applied == 0 && dropped == 0)
                {
                    // 0 applied AND 0 dropped — all skipped (permanent failures). Advancing
                    // would lose events. Break to surface the stall via /api/sync/status.
                    logger.LogWarning(
                        "Push to {Remote}: 0/{Total} events applied (skipped={Skipped}) in this chunk; leaving cursor at {After}",
                        remoteIdentity.NodeId, chunk.Count, skipped, pushAfter);
                    stalled = true;
                    break;
                }
                if (skipped > 0 && !lastAppliedSeq.HasValue)
                {
                    // A current server reports null when the first item was skipped, even if it
                    // later accepted items in this batch. Do not mistake that for the old-server
                    // fallback below: advancing to the chunk end would lose that first event.
                    logger.LogWarning(
                        "Push to {Remote}: the accepted prefix is empty (skipped={Skipped}); leaving cursor at {After}",
                        remoteIdentity.NodeId, skipped, pushAfter);
                    stalled = true;
                    break;
                }
                if (lastAppliedSeq.HasValue)
                {
                    if (lastAppliedSeq.Value > pushAfter)
                        pushAfter = lastAppliedSeq.Value;
                    if (applied + dropped < chunk.Count) { stalled = true; break; } // some skipped — stop, re-push next cycle
                }
                else
                {
                    // Older server: no per-event detail (case 3). Advance to the end of the chunk.
                    // An older peer which sends the historical, non-contiguous value cannot be
                    // recognized per event on protocol 3, so its reported position must still be
                    // trusted. Upgrading that peer is required to close that legacy limitation.
                    pushAfter = chunk[^1].SequenceNum;
                }
            }
            if (stalled) break;

            if (page.Count < PushFetchSize) break;
        }

        if (totalApplied + totalDropped > 0)
        {
            await pushPositionRepo.UpsertAsync(new SyncPushPosition
            {
                RemoteNodeId = remoteIdentity.NodeId,
                LastPushedSeq = pushAfter,
                PushedAt = DateTime.UtcNow
            });
            logger.LogInformation("Push: applied {Applied}, dropped {Dropped}, skipped {Skipped} on {NodeId}",
                totalApplied, totalDropped, totalSkipped, remoteIdentity.NodeId);
        }

        return appliedCount;
    }

    private static async Task<RemoteIdentityDto> GetRemoteIdentityAsync(
        HttpClient http, string baseUrl, CancellationToken ct)
    {
        var resp = await http.GetAsync($"{baseUrl}/api/sync/identity", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RemoteIdentityDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid identity response.");
    }

    // Delegates to the shared PeerAuthenticator helper — same flow, single source of truth,
    // also used by the reachability self-test (POST /api/sync/probe) endpoint.
    private Task<string> AuthenticateAsync(
        HttpClient http, string baseUrl, NodeIdentity identity, Guid expectedServerNodeId, CancellationToken ct)
        => PeerAuthenticator.AuthenticateAsync(authSigner, http, baseUrl, identity, expectedServerNodeId, ct);

    private static async Task<List<SyncEvent>> PullEventsAsync(
        HttpClient http, string baseUrl, string token, long afterSequence, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/sync/events?afterSequence={afterSequence}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 410)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            long lastCp = 0, headSeq = 0;
            string? code = null;
            var headReported = false;
            string msg = "Your position is older than remote retained history.";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // A member of another type is treated as absent, never as a number: the values decide what a
                    // full node may adopt from a blind peer (SyncWithPeerAsync), so they are read strictly.
                    if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String) code = err.GetString();
                    if (root.TryGetProperty("last_compaction_cp", out var cp) && cp.ValueKind == JsonValueKind.Number && cp.TryGetInt64(out var c)) lastCp = c;
                    if (root.TryGetProperty("current_head_seq", out var head) && head.ValueKind == JsonValueKind.Number && head.TryGetInt64(out var h))
                    {
                        headSeq = h;
                        headReported = true;
                    }
                    if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString() ?? msg;
                }
            }
            catch (JsonException) { }
            throw new SnapshotRequiredException(baseUrl, lastCp, headSeq, msg, code, headReported);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<SyncEvent>>(JsonOpts, ct)
            ?? [];
    }

    /// <summary>
    /// Whether an event claiming to come from this node really is one of ours: it is already in our
    /// own log, or it carries a signature that verifies under our own public key. The signature covers
    /// the event id, node id, lamport time, type, entity, payload, protocol version and creation time
    /// (<see cref="EventSignature.BuildPayload"/>), so it cannot be produced without our private key —
    /// which is what makes skipping such an event safe rather than merely convenient. Everything else
    /// — including an event that names us but carries a signature we cannot check — is left to
    /// <see cref="EventApplier"/>, whose answer for an unverifiable one is the ordinary refusal.
    ///
    /// <para>The signature is also what covers the compacted case: an event we authored, whose row a
    /// compaction has since removed, is no longer in our log but is still ours beyond doubt.</para>
    /// </summary>
    private async Task<bool> IsProvenOwnAsync(SyncEvent evt, NodeIdentity identity)
    {
        if (await eventLogRepo.ExistsAsync(evt.EventId)) return true;
        return identity.Ed25519PublicKey is { Length: > 0 }
            && Ed25519Signer.Verify(identity.Ed25519PublicKey, EventSignature.BuildPayload(evt), evt.Signature);
    }

    private async Task ReportPositionAsync(
        HttpClient http, string baseUrl, string token, long sequence, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, 
                $"{baseUrl}/api/sync/report-position?sequence={sequence}&protocolVersion={SyncProtocolVersion.Current}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Non-critical, don't fail the whole sync
            logger.LogWarning(ex, "Failed to report position to {Base}", baseUrl);
        }
    }

    private static async Task<ApplyResultDto> PushEventsAsync(
        HttpClient http, string baseUrl, string token, List<SyncEvent> events, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync/events");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(events, options: JsonOpts);

        var resp = await http.SendAsync(req, ct);
        // Report 413 as data instead of throwing, so the caller (PushChunkWithSplitAsync) can
        // split and retry. Throwing would unwind the whole push loop with the cursor unchanged,
        // and the next cycle would resend the same request and 413 again, forever.
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
            return new ApplyResultDto(0, 0, null, 0, TooLarge: true);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ApplyResultDto>(JsonOpts, ct)
            ?? new ApplyResultDto(0, 0);
    }

    /// <summary>
    /// Pushes one chunk, splitting and retrying if the peer reports it as too large (413) — the
    /// safety net behind client-side size-bounded batching. SplitIntoByteBoundedBatches should
    /// keep an ordinary chunk well under the server's per-request cap, but a config mismatch (e.g. a
    /// future MediaService size limit raised without a matching bump to the cap enforced in
    /// SyncEndpoints.cs) must still degrade gracefully instead of resending the identical oversized
    /// request forever, which would wedge the push permanently. Halves the
    /// chunk and recurses; a single event that STILL 413s alone is quarantined: logged loudly and
    /// counted as permanently skipped rather than retried every cycle with no operator-visible
    /// signal beyond a log line.
    /// </summary>
    private async Task<(int Applied, int Skipped, int Dropped, long? LastAppliedSequence)> PushChunkWithSplitAsync(
        HttpClient http, string baseUrl, string token, List<SyncEvent> chunk, CancellationToken ct)
    {
        var result = await PushEventsAsync(http, baseUrl, token, chunk, ct);
        if (!result.TooLarge)
            return (result.Applied, result.Skipped, result.Dropped, result.LastAppliedSequence);

        if (chunk.Count == 1)
        {
            var evt = chunk[0];
            logger.LogError(
                "Push: event {Seq} ({EventId}, {Type}) was rejected as too large to push even alone " +
                "({Chars} payload chars) — quarantining: skipping it permanently instead of retrying " +
                "forever. This needs operator attention (server per-request cap and actual event size " +
                "are mismatched). See GET /api/sync/quarantine.",
                evt.SequenceNum, evt.EventId, evt.EventType, evt.Payload?.Length ?? 0);
            var quarantined = await SyncEventQuarantine.RecordPermanentFailureUntilQuarantinedAsync(
                quarantineRepo, evt.EventId, evt.EventType, evt.NodeId,
                "Rejected as too large to push (413), even alone.");

            // A 413 for a one-event request cannot be resolved by retrying. It is deliberately
            // consumed immediately, even though the generic failure tracker has not yet reached
            // its retry threshold; otherwise the push cursor would remain before this event and
            // block every later event forever.
            logger.LogDebug("Push: too-large event {EventId} recorded as quarantined={Quarantined}",
                evt.EventId, quarantined);
            return (0, 0, 1, evt.SequenceNum);
        }

        var mid = chunk.Count / 2;
        var a = await PushChunkWithSplitAsync(http, baseUrl, token, chunk[..mid], ct);
        var b = await PushChunkWithSplitAsync(http, baseUrl, token, chunk[mid..], ct);
        // The second half can extend the prefix only after the first half accepted every item. If
        // the first half skipped an item, returning B's later position would jump the cursor over
        // that skipped item. Re-sending B is safe: EventApplier reports an existing event as
        // Applied without applying its data again.
        var firstHalfAccepted = a.Applied + a.Dropped == mid;
        return (a.Applied + b.Applied, a.Skipped + b.Skipped, a.Dropped + b.Dropped,
            firstHalfAccepted ? b.LastAppliedSequence ?? a.LastAppliedSequence : a.LastAppliedSequence);
    }

    /// <summary>
    /// Splits a page of candidate events into HTTP-request-sized chunks, bounded by cumulative
    /// payload size rather than just count — a fixed event-COUNT batch can still silently
    /// exceed the server's per-request size cap (a single media_create event can carry up to ~20MB
    /// of base64 ciphertext alone) and get stuck retrying an oversized request forever. Uses
    /// SyncEvent.Payload.Length (the JSON payload string's UTF-16 char count) as a size estimate —
    /// close enough for base64-heavy content; exactness isn't the goal, just staying safely under
    /// the hard cap with margin. A single event bigger than byteTarget on its own still gets its own
    /// one-event chunk (it can't be split further) — PushChunkWithSplitAsync is the fallback if even
    /// that turns out to be too large for the peer.
    /// </summary>
    private static IEnumerable<List<SyncEvent>> SplitIntoByteBoundedBatches(List<SyncEvent> events, long byteTarget)
    {
        var current = new List<SyncEvent>();
        long currentSize = 0;
        foreach (var evt in events)
        {
            var evtSize = evt.Payload?.Length ?? 0;
            if (current.Count > 0 && currentSize + evtSize > byteTarget)
            {
                yield return current;
                current = [];
                currentSize = 0;
            }
            current.Add(evt);
            currentSize += evtSize;
        }
        if (current.Count > 0) yield return current;
    }

    // Local DTOs for remote API responses
    private sealed record RemoteIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion = 0);
    // LastAppliedSequence is nullable for backward compat with older servers — fall back to
    // advancing to the batch end (batch[^1]) if absent. Current servers always populate it (see
    // SyncApplyResult in BeeMemoryBank.Api.Models).
    // TooLarge is a purely local (client-side) marker for a 413 response — it never round-trips
    // through JSON (the server never returns it; PushEventsAsync constructs it directly), so it's
    // fine that ReadFromJsonAsync<ApplyResultDto> would leave it false by default on every real
    // deserialized response.
    private sealed record ApplyResultDto(int Applied, int Skipped, long? LastAppliedSequence = null, int Dropped = 0, bool TooLarge = false);
}

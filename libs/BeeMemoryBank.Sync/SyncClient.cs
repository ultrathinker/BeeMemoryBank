using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
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
    SessionService sessionService,
    INodeAuthSigner authSigner,
    ILogger<SyncClient> logger,
    PeerNewerProtocolState peerNewerProtocolState,
    ISyncQuarantineRepository quarantineRepo,
    IRestoreRetrier? restoreRetrier = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Synchronizes with a remote node. Returns the number of new events applied locally.
    /// </summary>
    /// <param name="expectedPeerNodeId">
    /// The whitelist entry's NodeId for the peer being dialed — the audience anchor for M6's
    /// challenge-relay protection. It is a required parameter rather than something resolved in
    /// here precisely because it must come from a source the peer does not control: every caller
    /// (SyncScheduler, and the mobile InitialSyncPage/SyncWorker/SyncStatusService) is already
    /// iterating tbl_whitelist rows and has it in hand. An earlier version looked it up by
    /// matching remoteApiBase against tbl_whitelist.api_address, which quietly resolved to
    /// "nothing pinned" for any caller that passed the address in a different shape — and the
    /// only safe thing to do with "nothing pinned" is refuse, so a string mismatch became a
    /// sync outage. Passing the id explicitly removes the string comparison from the trust path.
    /// </param>
    public async Task<int> SyncWithAsync(
        HttpClient http, string remoteApiBase, Guid expectedPeerNodeId, CancellationToken ct = default)
    {
        // Belt-and-suspenders for bug #5: in addition to the unlock-time sweep in
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

        // 0. Verify DEK compatibility via sentinel
        await VerifyRemoteSentinelAsync(http, remoteApiBase, identity, ct);

        // 1. Get remote node identity
        var remoteIdentity = await GetRemoteIdentityAsync(http, remoteApiBase, ct);
        logger.LogDebug("Synchronizing with {NodeId} ({Base})", remoteIdentity.NodeId, remoteApiBase);

        // 2. Authentication. The audience anchor for M6's challenge-relay protection is
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

        var token = await AuthenticateAsync(http, remoteApiBase, identity, expectedPeerNodeId, ct);

        int appliedCount = 0;

        if (remoteIdentity.ProtocolVersion > SyncProtocolVersion.Current)
        {
            logger.LogWarning("Remote node {NodeId} has a newer protocol version ({RemoteVersion} > {LocalVersion}). Skipping pull-and-apply.",
                remoteIdentity.NodeId, remoteIdentity.ProtocolVersion, SyncProtocolVersion.Current);
            peerNewerProtocolState.HasNewerProtocol = true;
        }
        else
        {
            peerNewerProtocolState.HasNewerProtocol = false;

            // 3. Pull: download new events from the remote node
            var position = await syncPositionRepo.GetAsync(remoteIdentity.NodeId);
            var afterSeq = position?.LastSequenceNum ?? 0;

            var remoteEvents = await PullEventsAsync(http, remoteApiBase, token, afterSeq, ct);
            logger.LogDebug("Received {Count} events from {NodeId}", remoteEvents.Count, remoteIdentity.NodeId);

            long lastApplied = afterSeq;
            int droppedCount = 0;
            foreach (var evt in remoteEvents)
            {
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
                    // M5c: a genuinely broken event (bad signature, whitelist ordering, any other
                    // permanent failure) used to stop the WHOLE page here, every cycle, forever —
                    // the cursor never moved past it, so no event after it in this page (or any
                    // later pull) ever got a chance either. Once the SAME event has failed
                    // QuarantineThreshold times in a row, treat it as permanently skipped instead:
                    // advance past it and keep going, rather than wedging the entire pull behind
                    // one bad event. A merely transient failure (network blip, momentarily-locked
                    // local DB) hasn't built up a streak yet and still gets the old
                    // stop-and-retry-from-here behavior.
                    var quarantined = await SyncEventQuarantine.RecordFailureAsync(quarantineRepo, evt.EventId, evt.EventType, evt.NodeId, ex.Message);
                    if (quarantined)
                    {
                        logger.LogError(ex,
                            "QUARANTINED event {Seq} ({EventId}, {Type}) after {N} consecutive failures — " +
                            "skipping permanently. See GET /api/sync/quarantine.",
                            evt.SequenceNum, evt.EventId, evt.EventType, SyncEventQuarantine.QuarantineThreshold);
                        lastApplied = evt.SequenceNum;
                        droppedCount++;
                        continue;
                    }

                    logger.LogError(ex, "Failed to apply event {Seq} from remote, stopping sync. Will retry from this position.", evt.SequenceNum);
                    break;
                }
            }

            if (remoteEvents.Count > 0)
            {
                await syncPositionRepo.UpsertAsync(new SyncPosition
                {
                    RemoteNodeId = remoteIdentity.NodeId,
                    LastSequenceNum = lastApplied,
                    UpdatedAt = DateTime.UtcNow
                });
                logger.LogInformation("Pull: applied {Applied}, dropped {Dropped}. Position: {Seq}",
                    appliedCount, droppedCount, lastApplied);
            }

            // Always report our current position back to the remote — even when we're fully caught up
            // and there were no new events. Otherwise the remote never learns our position and shows
            // "Waiting for first sync — Never" forever, and compaction thinks we have no active peers.
            await ReportPositionAsync(http, remoteApiBase, token, lastApplied, ct);
        }

        // 4. Push: relay all events to the remote node (excluding its own events)
        //
        // M5: pushed HTTP requests are now bounded by cumulative payload SIZE
        // (SplitIntoByteBoundedBatches), not just event count — a fixed 500-event batch could
        // still embed a single ~20MB media_create event (~27MB once base64-encoded), and the old
        // 10MB server-side guard never actually caught it: JsonContent sends a chunked body with
        // no Content-Length, which is all that guard checked, so it silently did nothing against
        // the real client and only bit if some buffering proxy added a Content-Length header. When
        // it did, the peer 413'd and this loop retried the IDENTICAL oversized batch forever — a
        // permanent push wedge. PushChunkWithSplitAsync is the safety net if a chunk is still
        // rejected as too large despite the size-aware split (e.g. a config mismatch between this
        // node's MediaService size limit and the peer's per-request cap): it halves and retries
        // rather than resending the same request forever, quarantining a single event that's still
        // too large alone (see SyncEventQuarantine and GET /api/sync/quarantine).
        const int PushFetchSize = 500;
        const long PushBatchByteTarget = 8 * 1024 * 1024;
        var pushPosition = await pushPositionRepo.GetAsync(remoteIdentity.NodeId);
        long pushAfter = pushPosition?.LastPushedSeq ?? 0;
        int totalApplied = 0, totalSkipped = 0;
        long localMaxSeq = await eventLogRepo.GetMaxSequenceAsync();
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
                var (applied, skipped, dropped, lastAppliedSeq) =
                    await PushChunkWithSplitAsync(http, remoteApiBase, token, chunk, ct);
                totalApplied += applied;
                totalSkipped += skipped;

                // Advance the cursor only as far as the remote actually applied. If the remote
                // skipped event N (signature, schema, replay shield, etc.) and applied N+1, the
                // old code (`pushAfter = chunk[^1].SequenceNum`) would jump past N permanently —
                // N is gone from the pusher's view and the remote never sees it again.
                //
                // Three cases:
                //   1. New server, Applied > 0 → use LastAppliedSequence. Stops past the last
                //      successfully-applied event. Skipped events stay in our outbox until either
                //      they get applied on the remote or admin intervenes.
                //   2. New server, Applied == 0 → LastAppliedSequence is null AND nothing landed.
                //      Don't advance; break to surface the stall via /api/sync/status.
                //   3. Old server (LastAppliedSequence absent in JSON, deserializes to null) but
                //      Applied > 0 → no per-event detail available, use legacy chunk[^1] behaviour.
                //      Old server can't return Applied > 0 with LastAppliedSequence == null on the
                //      new client because old server was buggy in exactly the way #3 fixes — but
                //      for any in-flight transition cluster, treating "got something, no detail"
                //      as "advance to end of chunk" matches old semantics.
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
                if (lastAppliedSeq.HasValue)
                {
                    if (lastAppliedSeq.Value > pushAfter)
                        pushAfter = lastAppliedSeq.Value;
                    if (applied < chunk.Count) { stalled = true; break; } // some skipped — stop, re-push next cycle
                }
                else
                {
                    // Pre-fix server: no per-event detail. Match legacy behaviour.
                    pushAfter = chunk[^1].SequenceNum;
                }
            }
            if (stalled) break;

            if (page.Count < PushFetchSize) break;
        }

        if (totalApplied + totalSkipped > 0)
        {
            await pushPositionRepo.UpsertAsync(new SyncPushPosition
            {
                RemoteNodeId = remoteIdentity.NodeId,
                LastPushedSeq = pushAfter,
                PushedAt = DateTime.UtcNow
            });
            logger.LogInformation("Push: applied {Applied}, skipped {Skipped} on {NodeId}",
                totalApplied, totalSkipped, remoteIdentity.NodeId);
        }

        return appliedCount;
    }

    private async Task VerifyRemoteSentinelAsync(
        HttpClient http, string baseUrl, NodeIdentity identity, CancellationToken ct)
    {
        // Sentinel verification needs the master DEK. On the locked background backup path
        // (mobile ingest signs via Keystore, vault stays locked) the DEK isn't available —
        // skip this best-effort sanity check. The pull/apply path stores ciphertext and
        // never needs the DEK, so backup-sync proceeds safely without it.
        if (!sessionService.IsUnlocked) return;
        try
        {
            var resp = await http.GetAsync($"{baseUrl}/api/sync/sentinel", ct);
            if (!resp.IsSuccessStatusCode) return; // server without sentinel — skip check

            var dto = await resp.Content.ReadFromJsonAsync<SentinelDto>(JsonOpts, ct);
            if (dto?.SentinelB64 == null) return;

            var remoteSentinel = Convert.FromBase64String(dto.SentinelB64);
            var localDek = sessionService.GetMasterDek();
            try
            {
                if (!MasterKeyManager.VerifySentinel(remoteSentinel, localDek))
                {
                    // Sentinel mismatch USED to throw immediately — but that prevented us from
                    // pulling DEK_ROTATION_COMMIT events that would catch us up. After auto-
                    // accept (peer-acceptance model), an honest peer that rotated their DEK is
                    // expected to look like a sentinel mismatch UNTIL we apply their COMMIT.
                    // Now: log warning and proceed; let event pull deliver the rotation event,
                    // and either auto-accept (per whitelist flag) or queue for manual accept.
                    // If after the pull we still mismatch and no rotation event arrived, the
                    // next cycle will retry the same warning. (Found by E2E test — a peer that
                    // joined before a rotation could never receive the rotation event.)
                    logger.LogWarning(
                        "DEK sentinel mismatch with {BaseUrl}; proceeding with event pull anyway — peer may have a pending DEK rotation we need to apply.",
                        baseUrl);
                }
            }
            finally { Array.Clear(localDek); }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentinel check failed for {Base} — skipping", baseUrl);
        }
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
    // now also reused by the reachability self-test (POST /api/sync/probe) endpoint.
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
            string msg = "Your position is older than remote retained history.";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("last_compaction_cp", out var cp)) lastCp = cp.GetInt64();
                if (doc.RootElement.TryGetProperty("current_head_seq", out var head)) headSeq = head.GetInt64();
                if (doc.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? msg;
            }
            catch { }
            throw new SnapshotRequiredException(baseUrl, lastCp, headSeq, msg);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<SyncEvent>>(JsonOpts, ct)
            ?? [];
    }

    private async Task ReportPositionAsync(
        HttpClient http, string baseUrl, string token, long sequence, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, 
                $"{baseUrl}/api/sync/report-position?sequence={sequence}");
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
        // M5: report 413 as data instead of throwing, so the caller (PushChunkWithSplitAsync) can
        // split and retry rather than have the whole push loop unwind on an HttpRequestException —
        // that used to leave the cursor exactly where it was, so the next cycle resent the same
        // request and 413'd again, forever.
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
            return new ApplyResultDto(0, 0, null, 0, TooLarge: true);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ApplyResultDto>(JsonOpts, ct)
            ?? new ApplyResultDto(0, 0);
    }

    /// <summary>
    /// Pushes one chunk, splitting and retrying if the peer reports it as too large (413) — the
    /// safety net behind client-side size-bounded batching (M5). SplitIntoByteBoundedBatches should
    /// keep an ordinary chunk well under the server's per-request cap, but a config mismatch (e.g. a
    /// future MediaService size limit raised without a matching bump to the cap enforced in
    /// SyncEndpoints.cs) must still degrade gracefully instead of resending the identical oversized
    /// request forever — the exact permanent-wedge failure mode this finding closes. Halves the
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
            await SyncEventQuarantine.RecordFailureAsync(quarantineRepo, evt.EventId, evt.EventType, evt.NodeId, "Rejected as too large to push (413), even alone.");
            return (0, 1, 0, evt.SequenceNum);
        }

        var mid = chunk.Count / 2;
        var a = await PushChunkWithSplitAsync(http, baseUrl, token, chunk[..mid], ct);
        var b = await PushChunkWithSplitAsync(http, baseUrl, token, chunk[mid..], ct);
        // The second half's LastAppliedSequence wins when present — it covers the later-sequenced
        // half. If it 413'd down to nothing (Dropped/Skipped only, no LastAppliedSequence) but the
        // first half made progress, keep the first half's position instead of losing it.
        return (a.Applied + b.Applied, a.Skipped + b.Skipped, a.Dropped + b.Dropped,
            b.LastAppliedSequence ?? a.LastAppliedSequence);
    }

    /// <summary>
    /// Splits a page of candidate events into HTTP-request-sized chunks, bounded by cumulative
    /// payload size (M5) rather than just count — a fixed event-COUNT batch can still silently
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
    private sealed record SentinelDto(string? SentinelB64);
    private sealed record RemoteIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion = 0);
    // LastAppliedSequence is nullable for backward compat with older servers — fall back to
    // the prior batch[^1] behaviour if absent. New servers always populate it (see
    // SyncApplyResult in BeeMemoryBank.Api.Models). (Brainstorm bug #3.)
    // TooLarge (M5) is a purely local (client-side) marker for a 413 response — it never round-trips
    // through JSON (the server never returns it; PushEventsAsync constructs it directly), so it's
    // fine that ReadFromJsonAsync<ApplyResultDto> would leave it false by default on every real
    // deserialized response.
    private sealed record ApplyResultDto(int Applied, int Skipped, long? LastAppliedSequence = null, int Dropped = 0, bool TooLarge = false);
}

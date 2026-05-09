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
    ILogger<SyncClient> logger,
    IRestoreRetrier? restoreRetrier = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Synchronizes with a remote node. Returns the number of new events applied locally.
    /// </summary>
    public async Task<int> SyncWithAsync(HttpClient http, string remoteApiBase, CancellationToken ct = default)
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

        // 2. Authentication
        var token = await AuthenticateAsync(http, remoteApiBase, identity, ct);

        // 3. Pull: download new events from the remote node
        var position = await syncPositionRepo.GetAsync(remoteIdentity.NodeId);
        var afterSeq = position?.LastSequenceNum ?? 0;

        var remoteEvents = await PullEventsAsync(http, remoteApiBase, token, afterSeq, ct);
        logger.LogDebug("Received {Count} events from {NodeId}", remoteEvents.Count, remoteIdentity.NodeId);

        long lastApplied = afterSeq;
        int appliedCount = 0;
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
            }
            catch (Exception ex)
            {
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

        // 4. Push: relay all events to the remote node (excluding its own events)
        const int PushBatchSize = 500;
        var pushPosition = await pushPositionRepo.GetAsync(remoteIdentity.NodeId);
        long pushAfter = pushPosition?.LastPushedSeq ?? 0;
        int totalApplied = 0, totalSkipped = 0;
        long localMaxSeq = await eventLogRepo.GetMaxSequenceAsync();
        logger.LogInformation("Push to {Remote}: localMaxSeq={MaxSeq}, pushAfter={After}", remoteIdentity.NodeId, localMaxSeq, pushAfter);
        while (true)
        {
            var batch = await eventLogRepo.GetEventsToRelayAsync(remoteIdentity.NodeId, pushAfter, PushBatchSize);
            logger.LogInformation("Push batch to {Remote}: {Count} events", remoteIdentity.NodeId, batch.Count);
            if (batch.Count == 0) break;

            var result = await PushEventsAsync(http, remoteApiBase, token, batch, ct);
            totalApplied += result.Applied;
            totalSkipped += result.Skipped;

            // Advance the cursor only as far as the remote actually applied. If the remote
            // skipped event N (signature, schema, replay shield, etc.) and applied N+1, the
            // old code (`pushAfter = batch[^1].SequenceNum`) would jump past N permanently —
            // N is gone from the pusher's view and the remote never sees it again.
            //
            // Three cases:
            //   1. New server, Applied > 0 → use LastAppliedSequence. Stops past the last
            //      successfully-applied event. Skipped events stay in our outbox until either
            //      they get applied on the remote or admin intervenes.
            //   2. New server, Applied == 0 → LastAppliedSequence is null AND nothing landed.
            //      Don't advance; break to surface the stall via /api/sync/status.
            //   3. Old server (LastAppliedSequence absent in JSON, deserializes to null) but
            //      Applied > 0 → no per-event detail available, use legacy batch[^1] behaviour.
            //      Old server can't return Applied > 0 with LastAppliedSequence == null on the
            //      new client because old server was buggy in exactly the way #3 fixes — but
            //      for any in-flight transition cluster, treating "got something, no detail"
            //      as "advance to end of batch" matches old semantics.
            if (result.Applied == 0 && result.Dropped == 0)
            {
                // 0 applied AND 0 dropped — all skipped (permanent failures). Advancing
                // would lose events. Break to surface the stall via /api/sync/status.
                logger.LogWarning(
                    "Push to {Remote}: 0/{Total} events applied (skipped={Skipped}); leaving cursor at {After}",
                    remoteIdentity.NodeId, batch.Count, result.Skipped, pushAfter);
                break;
            }
            if (result.LastAppliedSequence.HasValue)
            {
                if (result.LastAppliedSequence.Value > pushAfter)
                    pushAfter = result.LastAppliedSequence.Value;
                if (result.Applied < batch.Count) break; // some skipped — stop, re-push next cycle
            }
            else
            {
                // Pre-fix server: no per-event detail. Match legacy behaviour.
                pushAfter = batch[^1].SequenceNum;
            }

            if (batch.Count < PushBatchSize) break;
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

    private async Task<string> AuthenticateAsync(
        HttpClient http, string baseUrl, NodeIdentity identity, CancellationToken ct)
    {
        // Get challenge
        var challengeResp = await http.PostAsync($"{baseUrl}/api/sync/challenge", null, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid challenge response.");

        // Sign challenge with domain tag (server-side verifier requires tagged form).
        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
        var challengePayload = domainTag.Concat(challengeBytes).ToArray();
        byte[] signature;
        if (identity.Ed25519PrivateKeyV == 0)
        {
            signature = NodeIdentityCrypto.SignWithIdentity(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, Array.Empty<byte>(), challengePayload);
        }
        else
        {
            var masterDek = sessionService.GetMasterDek();
            try
            {
                signature = NodeIdentityCrypto.SignWithIdentity(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, masterDek, challengePayload);
            }
            finally
            {
                Array.Clear(masterDek);
            }
        }

        // Authenticate
        var authResp = await http.PostAsJsonAsync($"{baseUrl}/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature)
        }, ct);
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid authenticate response.");

        return authData.Token;
    }

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
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ApplyResultDto>(JsonOpts, ct)
            ?? new ApplyResultDto(0, 0);
    }

    // Local DTOs for remote API responses
    private sealed record SentinelDto(string? SentinelB64);
    private sealed record RemoteIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);
    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
    // LastAppliedSequence is nullable for backward compat with older servers — fall back to
    // the prior batch[^1] behaviour if absent. New servers always populate it (see
    // SyncApplyResult in BeeMemoryBank.Api.Models). (Brainstorm bug #3.)
    private sealed record ApplyResultDto(int Applied, int Skipped, long? LastAppliedSequence = null, int Dropped = 0);
}

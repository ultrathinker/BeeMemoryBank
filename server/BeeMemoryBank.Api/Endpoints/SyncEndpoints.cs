using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using Microsoft.Extensions.Logging;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

public static class SyncEndpoints
{
    // JSON options for deserializing peer-to-peer relay responses. Matches the API's global
    // ConfigureHttpJsonOptions (web defaults + JsonStringEnumConverter) so enum-typed relay
    // DTOs round-trip correctly — HttpContent.ReadFromJsonAsync uses plain web defaults by
    // default, which would choke on string-serialized enums.
    private static readonly JsonSerializerOptions PeerJsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // L9: /api/sync/challenge is intentionally unauthenticated (a peer needs a challenge before it
    // can prove anything) and, unlike every other sync endpoint, has no Bearer-token gate to bound
    // request volume. Each call allocates a ChallengeEntry plus a CSPRNG-filled 32-byte buffer; the
    // 60s TTL bounds how long any one entry lives, but nothing previously bounded the ALLOCATION
    // rate within that window. Reuses the same SlidingWindowRateLimiter the API's RateLimitMiddleware
    // and the Web layer's public-endpoint limiter use, keyed per-IP like RateLimitMiddleware — a
    // generous budget, since this endpoint is legitimately hit once per sync cycle by every peer in
    // the mesh, each from a different IP with its own independent bucket.
    private static readonly SlidingWindowRateLimiter ChallengeLimiter = new(30, TimeSpan.FromMinutes(1));

    // M5a: sized to comfortably fit a single legitimate event — MediaService's 20MB max file size,
    // base64-expanded (~4/3x, ~27MB), plus JSON envelope overhead for the rest of the SyncEvent's
    // fields and the enclosing array. Matches what SyncClient's own size-aware push batching
    // (SplitIntoByteBoundedBatches / PushChunkWithSplitAsync) assumes "too large" means.
    private const long PushMaxRequestBytes = 32L * 1024 * 1024;

    // M5b: pull responses are also bounded by cumulative payload size, not just event count — see
    // the /api/sync/events GET handler below.
    private const long PullResponseByteTarget = 32L * 1024 * 1024;

    public static void MapSyncEndpoints(this WebApplication app)
    {
        // ─── Identity (no auth) ─────────────────────────────────────────────────
        app.MapGet("/api/sync/identity", async (INodeIdentityRepository nodeRepo) =>
        {
            var identity = await nodeRepo.GetAsync();
            if (identity == null) return Results.Problem("Node is not initialized.", statusCode: 503);
            return Results.Ok(new SyncIdentityResponse(
                identity.NodeId,
                identity.DisplayName,
                Convert.ToBase64String(identity.Ed25519PublicKey),
                SyncProtocolVersion.Current));
        }).WithTags("Sync");

        // ─── Sentinel (no auth — encrypted, useless without DEK) ───────────────
        app.MapGet("/api/sync/sentinel", async (INodeIdentityRepository nodeRepo) =>
        {
            var sentinel = await nodeRepo.GetSentinelAsync();
            if (sentinel == null) return Results.NotFound();
            return Results.Ok(new { sentinelB64 = Convert.ToBase64String(sentinel) });
        }).WithTags("Sync");

        // ─── Challenge ───────────────────────────────────────────────────────────
        app.MapPost("/api/sync/challenge", async (
            HttpContext ctx,
            SyncTokenStore store,
            INodeIdentityRepository nodeRepo) =>
        {
            // L9: per-IP throttle — see ChallengeLimiter's doc comment. Uses the raw connection IP,
            // same as RateLimitMiddleware, not the GDPR-masked one MaskIp produces for logging (that
            // would bucket a whole /24 together and let one IP in a subnet exhaust another's budget).
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!ChallengeLimiter.TryAcquire(ip))
                return Results.StatusCode(429);

            var identity = await nodeRepo.GetAsync();
            if (identity == null) return Results.Problem("Node is not initialized.", statusCode: 503);
            var challenge = store.IssueChallenge(identity.NodeId);
            return Results.Ok(new SyncChallengeResponse(challenge, identity.NodeId));
        }).WithTags("Sync");

        // ─── Authenticate ────────────────────────────────────────────────────────
        app.MapPost("/api/sync/authenticate", async (
            SyncAuthRequest req,
            SyncTokenStore store,
            IWhitelistRepository whitelist,
            ILoggerFactory loggerFactory,
            HttpContext httpCtx) =>
        {
            var logger = loggerFactory.CreateLogger("SyncAuthenticate");
            // Mask last octet (v4) / last 80 bits (v6) for GDPR-friendly logging — enough
            // signal to spot abuse patterns (per-/24 subnet, per-/48 prefix), insufficient
            // for identifying an individual host without correlating with other data.
            var remoteIp = MaskIp(httpCtx.Connection.RemoteIpAddress);

            // ConsumeChallenge hands back the NodeId THIS node stamped into the challenge when it
            // issued it (IssueChallenge(identity.NodeId) below) — our own real identity at that
            // moment, from server-side state, not anything the caller supplies. This is the audience
            // anchor for the M6 verification below: a signature is only accepted here if it was made
            // FOR THIS NODE, regardless of what req.NodeId or anything else in the request claims.
            if (!store.ConsumeChallenge(req.ChallengeB64, out var serverNodeId))
            {
                logger.LogWarning("Auth 401 from {Ip} for {NodeId}: challenge not found or expired", remoteIp, req.NodeId);
                return Results.Unauthorized();
            }

            var entry = await whitelist.GetByNodeIdAsync(req.NodeId);
            if (entry == null || entry.Status != "A")
            {
                logger.LogWarning("Auth 401 from {Ip} for {NodeId}: whitelist entry={HasEntry} status={Status}",
                    remoteIp, req.NodeId, entry != null, entry?.Status);
                return Results.Unauthorized();
            }

            byte[] challengeBytes;
            byte[] signature;
            try
            {
                challengeBytes = Convert.FromBase64String(req.ChallengeB64);
                signature = Convert.FromBase64String(req.SignatureB64);
            }
            catch
            {
                return Results.BadRequest("Invalid base64 format.");
            }

            // M6: domain-separated Ed25519 signature, now bound to OUR OWN NodeId (serverNodeId,
            // above) as well as the challenge bytes. Before this, the signed payload was just
            // "BMB-CHALLENGE-V1\0" + challenge with no audience binding at all — a malicious peer
            // (or a LAN MITM; plain-HTTP peers are realistic given mDNS discovery) that a victim
            // node authenticates TO could fetch a challenge from some unrelated third node C and
            // hand it to the victim as its own. The victim would sign it not knowing any better, and
            // whoever relayed it could redeem that signature AS THE VICTIM on C — full event-log
            // pull and join-snapshot access, on a node the victim never intended to talk to. Binding
            // our real NodeId into what's verified means a signature only verifies at the node it
            // was actually made for.
            //
            // V1 (unbound) is still accepted as a fallback for interop with a peer whose
            // PeerAuthenticator hasn't been upgraded past this fix yet — it only ever produces a V1
            // signature, and rejecting it outright would silently wedge sync with every node in the
            // mesh that hasn't upgraded. A peer still on V1 gets exactly the (lack of) protection it
            // had before this fix, no worse than today; retire this branch once the whole mesh has
            // upgraded.
            var domainTagV2 = "BMB-CHALLENGE-V2\0"u8.ToArray();
            var taggedPayloadV2 = domainTagV2.Concat(serverNodeId.ToByteArray()).Concat(challengeBytes).ToArray();
            var domainTagV1 = "BMB-CHALLENGE-V1\0"u8.ToArray();
            var taggedPayloadV1 = domainTagV1.Concat(challengeBytes).ToArray();
            var sigOk = Ed25519Signer.Verify(entry.Ed25519PublicKey, taggedPayloadV2, signature)
                || Ed25519Signer.Verify(entry.Ed25519PublicKey, taggedPayloadV1, signature);
            if (!sigOk)
            {
                logger.LogWarning("Auth 401 for {NodeId} ({Display}): Ed25519 signature verify failed (pubkey {PubLen}b, sig {SigLen}b)",
                    req.NodeId, entry.DisplayName, entry.Ed25519PublicKey.Length, signature.Length);
                return Results.Unauthorized();
            }

            var token = store.IssueToken(req.NodeId);
            return Results.Ok(new SyncAuthResponse(token));
        }).WithTags("Sync");

        // ─── Pull events ─────────────────────────────────────────────────────────
        app.MapGet("/api/sync/events", async (
            HttpContext ctx,
            SyncTokenStore store,
            IEventLogRepository eventLogRepo,
            ISyncPushPositionRepository pushPositionRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            long afterSequence = 0,
            int limit = 1000) =>
        {
            if (!TryAuth(ctx, store, out var nodeId)) return Results.Unauthorized();
            if (invisibleMode.IsInvisible) return Results.StatusCode(503);

            // M5b: clamp the caller-suppliable page size — a peer requesting an arbitrarily large
            // `limit` would otherwise force a correspondingly large single DB fetch before we ever
            // get a chance to size-bound the response below.
            limit = Math.Clamp(limit, 1, 1000);

            var lastCompactionCp = await eventLogRepo.GetLastCompactionCpAsync();
            if (lastCompactionCp != null && afterSequence < lastCompactionCp.Value)
            {
                var headSeq = await eventLogRepo.GetMaxSequenceAsync();
                return Results.Json(new
                {
                    error = "SEQUENCE_TOO_OLD",
                    last_compaction_cp = lastCompactionCp.Value,
                    current_head_seq = headSeq,
                    message = "Your position is older than the last compaction point. Wipe this node and rejoin via /Setup."
                }, statusCode: 410);
            }

            var events = await eventLogRepo.GetAfterSequenceAsync(afterSequence, limit);

            // M5b: even at the count-based `limit` (1000 by default), events aren't uniformly
            // sized — a burst of near-max-size media_create events (~27MB each once base64-encoded)
            // could otherwise balloon this single response to tens of gigabytes. Trim to a
            // cumulative byte budget, always keeping at least one event so pull still makes forward
            // progress; the client naturally continues from wherever its cursor lands on the next
            // sync cycle (SyncClient does one page per cycle, not an internal drain-everything
            // loop), so returning fewer than `limit` events here is not a correctness issue.
            if (events.Count > 0)
            {
                var trimmed = new List<SyncEvent>(events.Count);
                long size = 0;
                foreach (var evt in events)
                {
                    var evtSize = evt.Payload?.Length ?? 0;
                    if (trimmed.Count > 0 && size + evtSize > PullResponseByteTarget) break;
                    trimmed.Add(evt);
                    size += evtSize;
                }
                events = trimmed;
            }

            // Record the highest sequence we sent, so delivery-status knows this node is up to date
            if (events.Count > 0)
                await pushPositionRepo.UpdatePositionAsync(nodeId, events[^1].SequenceNum);

            return Results.Ok(events);
        }).WithTags("Sync");

        // ─── Snapshot for join ──────────────────────────────────────────────────
        app.MapGet("/api/sync/snapshot/for-join", async (
            HttpContext ctx,
            SyncTokenStore store,
            SnapshotService snapshotService,
            SnapshotJoinCache cache,
            IEventLogRepository eventLogRepo,
            ISyncPositionRepository syncPositionRepo,
            INodeIdentityRepository nodeRepo,
            ILamportClock clock,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            ILogger<Program> logger) =>
        {
            if (!TryAuth(ctx, store, out var requesterNodeId))
                return Results.Unauthorized();

            if (invisibleMode.IsInvisible)
                return Results.StatusCode(503);

            var existingPos = await syncPositionRepo.GetAsync(requesterNodeId);
            if (existingPos != null)
            {
                return Results.Json(new
                {
                    error = "ALREADY_SYNCED",
                    message = "This node already has sync position. Use /api/sync/events to catch up, or wipe locally and rejoin."
                }, statusCode: 409);
            }

            var cached = cache.TryGet();
            string filePath;
            string sigPath;
            long cpSeq;
            long lamportTs;
            Guid producerId;

            if (cached != null)
            {
                filePath = cached.FilePath;
                sigPath = cached.SignatureFilePath;
                cpSeq = cached.CpSeq;
                lamportTs = cached.LamportTs;
                producerId = cached.ProducerNodeId;
                logger.LogInformation("Serving cached snapshot (CP={Cp}) to node {Node}", cpSeq, requesterNodeId);
            }
            else
            {
                var headSeq = await eventLogRepo.GetMaxSequenceAsync();
                logger.LogInformation("Generating snapshot for node {Node} at CP={Cp}", requesterNodeId, headSeq);

                var snapInfo = await snapshotService.CreateAsync(
                    filterSecrets: true,
                    sign: true,
                    cpSequenceNum: headSeq,
                    encryptDb: false); // joining node has no master DEK yet — ship plaintext over auth'd TLS

                filePath = snapshotService.GetSnapshotPath(snapInfo.FileName);
                sigPath = $"{filePath}.sig";
                cpSeq = headSeq;
                lamportTs = clock.Current;
                var identity = await nodeRepo.GetAsync()
                    ?? throw new InvalidOperationException("Node not initialized");
                producerId = identity.NodeId;

                cache.Set(filePath, sigPath, cpSeq, producerId, lamportTs);
            }

            if (!File.Exists(filePath))
            {
                logger.LogWarning("Cached snapshot file missing: {Path}", filePath);
                cache.Invalidate();
                return Results.StatusCode(500);
            }

            var signatureBytes = File.Exists(sigPath) ? await File.ReadAllBytesAsync(sigPath) : Array.Empty<byte>();
            var signatureB64 = Convert.ToBase64String(signatureBytes);

            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"bmb-snapshot-join.tar.gz\"";
            ctx.Response.Headers["X-BMB-Snapshot-CP-Seq"] = cpSeq.ToString();
            ctx.Response.Headers["X-BMB-Snapshot-Lamport"] = lamportTs.ToString();
            ctx.Response.Headers["X-BMB-Snapshot-Producer"] = producerId.ToString();
            ctx.Response.Headers["X-BMB-Snapshot-Signature"] = signatureB64;

            return Results.File(filePath, "application/gzip", Path.GetFileName(filePath));
        }).WithTags("Sync");

        // ─── Report position (explicit ACK) ─────────────────────────────────────
        app.MapPost("/api/sync/report-position", async (
            HttpContext ctx,
            SyncTokenStore store,
            ISyncPushPositionRepository pushPositionRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            long sequence) =>
        {
            if (!TryAuth(ctx, store, out var nodeId)) return Results.Unauthorized();
            if (invisibleMode.IsInvisible) return Results.StatusCode(503);
            await pushPositionRepo.UpdatePositionAsync(nodeId, sequence);
            return Results.Ok();
        }).WithTags("Sync");

        // ─── Apply events (push from remote) ─────────────────────────────────────
        app.MapPost("/api/sync/events", async (
            HttpContext ctx,
            SyncTokenStore store,
            EventApplier applier,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            CallerScopeHolder scopeHolder,
            ILoggerFactory loggerFactory) =>
        {
            if (!TryAuth(ctx, store, out _)) return Results.Unauthorized();
            if (invisibleMode.IsInvisible) return Results.StatusCode(503);

            // M5a: `ctx.Request.ContentLength is > 10MB` used to be the only guard here, and it
            // did nothing against the real client — HttpClient sends a JsonContent body chunked,
            // with no Content-Length header at all, so this check silently never fired against a
            // normal push. It only ever fired behind a buffering proxy that added a Content-Length
            // header, and even then 10MB was too small: a single legitimate media_create event can
            // carry up to ~20MB of base64 ciphertext (~27MB once JSON-encoded) alone, so a WORKING
            // guard at that size would have permanently 413'd every push containing it (SyncClient
            // would retry the identical batch forever — see PushChunkWithSplitAsync's doc comment).
            //
            // Fix: set the actual Kestrel per-request body size limit, which is enforced against
            // bytes actually read off the stream regardless of Content-Length/chunking, to a cap
            // that comfortably fits the largest legitimate single event.
            var maxBodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (maxBodyFeature is { IsReadOnly: false })
                maxBodyFeature.MaxRequestBodySize = PushMaxRequestBytes;

            SyncEvent[] events;
            try
            {
                events = await ctx.Request.ReadFromJsonAsync<SyncEvent[]>() ?? [];
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == 413)
            {
                // Body exceeded PushMaxRequestBytes while reading — the pusher's
                // PushChunkWithSplitAsync reacts to this by halving and retrying, or quarantining a
                // single event that's still too large alone, rather than resending the same request
                // forever.
                return Results.StatusCode(413);
            }
            catch
            {
                return Results.BadRequest(new ErrorResponse("Invalid JSON"));
            }

            if (events.Length > 2000)
                return Results.BadRequest(new ErrorResponse("Batch too large (max 2000 events)"));

            // Sync peers are trusted — bypass per-user ACL guards that CallerScopeMiddleware
            // sets to an empty AllowList for non-user/non-agent requests.
            scopeHolder.Scope = SystemCallerScope.Instance;

            var logger = loggerFactory.CreateLogger("SyncEndpoints");
            int applied = 0, skipped = 0, dropped = 0;
            long? lastAppliedSeq = null;
            foreach (var evt in events)
            {
                try
                {
                    var result = await applier.ApplyAsync(evt);
                    if (result == EventApplyResult.SilentlyDropped)
                    {
                        dropped++;
                        lastAppliedSeq = evt.SequenceNum;
                    }
                    else
                    {
                        applied++;
                        lastAppliedSeq = evt.SequenceNum;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipped event {EventId} of type {EventType}", evt.EventId, evt.EventType);
                    skipped++;
                }
            }
            return Results.Ok(new SyncApplyResult(applied, skipped, lastAppliedSeq, dropped));
        }).WithTags("Sync");

        // ─── Sync status (for Web UI progress display) ─────────────────────────
        app.MapGet("/api/sync/status", async (
            HttpContext ctx,
            IEventLogRepository eventLogRepo,
            ISyncPositionRepository syncPositionRepo,
            IWhitelistRepository whitelistRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            INodeIdentityRepository nodeRepo,
            PeerNewerProtocolState peerNewerProtocolState) =>
        {
            var identity = await nodeRepo.GetAsync();
            var totalEvents = await eventLogRepo.GetTotalCountAsync();
            var positions = await syncPositionRepo.GetAllAsync();
            var whitelist = await whitelistRepo.GetAllActiveAsync();
            var remoteNodes = whitelist.Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();

            var nodeStatuses = new List<object>();
            foreach (var node in remoteNodes)
            {
                var pos = positions.FirstOrDefault(p => p.RemoteNodeId == node.NodeId);
                nodeStatuses.Add(new
                {
                    nodeId = node.NodeId,
                    displayName = node.DisplayName,
                    apiAddress = node.ApiAddress,
                    lastSyncedSequence = pos?.LastSequenceNum ?? 0,
                    lastSyncedAt = pos?.UpdatedAt,
                });
            }

            return Results.Ok(new
            {
                localNodeId = identity?.NodeId,
                localNodeName = identity?.DisplayName,
                totalLocalEvents = totalEvents,
                connectedNodes = remoteNodes.Count,
                isInvisible = invisibleMode.IsInvisible,
                peerNewerProtocol = peerNewerProtocolState.HasNewerProtocol,
                nodes = nodeStatuses
            });
        }).RequireInternalKey().WithTags("Sync");

        // ─── Ping (lightweight check if new events exist) ────────────────────────
        app.MapGet("/api/sync/ping", async (
            HttpContext ctx,
            IEventLogRepository eventLogRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            long afterSequence = 0) =>
        {
            if (invisibleMode.IsInvisible) return Results.StatusCode(503);
            var events = await eventLogRepo.GetAfterSequenceAsync(afterSequence, 1);
            if (events.Count == 0)
                return Results.NoContent();
            return Results.Ok(new { count = await eventLogRepo.GetTotalCountAsync() - afterSequence });
        }).RequireInternalKey().WithTags("Sync");

        app.MapGet("/api/sync/invisible", (
            HttpContext ctx,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);
            return Results.Ok(new { IsInvisible = invisibleMode.IsInvisible });
        }).RequireInternalKey().WithTags("Sync");

        // ─── Invisible Mode Toggle ───────────────────────────────────────────────
        app.MapPost("/api/sync/invisible", (
            HttpContext ctx,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            [Microsoft.AspNetCore.Mvc.FromBody] bool isInvisible) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            invisibleMode.IsInvisible = isInvisible;
            return Results.Ok();
        }).RequireInternalKey().WithTags("Sync");

        // ─── Delivery status (requires internal key — exposes node topology) ────
        app.MapGet("/api/sync/delivery-status", async (
            HttpContext ctx,
            IEventLogRepository eventLogRepo,
            ISyncPushPositionRepository pushPositionRepo,
            IWhitelistRepository whitelistRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            INodeIdentityRepository nodeRepo) =>
        {
            var identity = await nodeRepo.GetAsync();
            // Use the head sequence number (MAX), NOT COUNT(*): after compaction trims early
            // events, COUNT is lower than the head seq, so comparing a node's last_pushed_seq
            // (a sequence number) against COUNT falsely reports caught-up nodes as behind and —
            // worse — behind nodes as "Up to date" once their seq exceeds the shrunken count.
            var headSeq = await eventLogRepo.GetMaxSequenceAsync();
            var pushPositions = await pushPositionRepo.GetAllAsync();
            var whitelist = await whitelistRepo.GetAllActiveAsync();

            var statuses = new List<DeliveryNodeStatus>();
            // One COUNT query per node below — N is the number of ACTIVE whitelist nodes (a handful
            // in practice), so the per-node CountEventsAfterSequenceAsync is acceptable. Batch into a
            // single query if the active-node count ever grows large.
            foreach (var node in whitelist.Where(node => node.NodeId != identity?.NodeId)) // exclude self
            {
                var push = pushPositions.FirstOrDefault(p => p.RemoteNodeId == node.NodeId);
                var nodeType = string.IsNullOrEmpty(node.ApiAddress) ? "private" : "public";
                var lastPushed = push?.LastPushedSeq ?? 0;
                // Exact count of events the node hasn't received yet — robust to gaps left by
                // compaction (head - lastPushed would over-count deleted events).
                var unsyncedCount = await eventLogRepo.CountEventsAfterSequenceAsync(lastPushed);
                statuses.Add(new DeliveryNodeStatus(
                    node.NodeId,
                    node.DisplayName,
                    nodeType,
                    lastPushed,
                    headSeq,
                    unsyncedCount,
                    unsyncedCount == 0,
                    push?.PushedAt));
            }

            return Results.Ok(new DeliveryStatusResponse(identity?.NodeId, invisibleMode.IsInvisible, statuses));
        }).RequireInternalKey().WithTags("Sync");

        // ─── Quarantined events (M5c operator visibility) ───────────────────────
        // Events that have repeatedly failed to apply during pull — see SyncEventQuarantine's doc
        // comment for why this exists: before it, a permanently-broken event silently wedged the
        // whole pull loop behind it every cycle, forever, with nothing beyond a repeating log line
        // to notice it by. Internal-key-gated like the other diagnostic endpoints above (exposes
        // node/event topology, not sensitive content — event payloads aren't included).
        //
        // M5 follow-up: reads through ISyncQuarantineRepository now (persisted), not the static
        // in-memory dictionary this used to be — see SyncEventQuarantine's updated doc comment for
        // why that was a real gap (a restart re-opened a stall it looked like it had just fixed).
        app.MapGet("/api/sync/quarantine", async (ISyncQuarantineRepository quarantineRepo) =>
        {
            var entries = await SyncEventQuarantine.ListAllAsync(quarantineRepo);
            return Results.Ok(entries);
        }).RequireInternalKey().WithTags("Sync");

        // ─── Clear / retry a quarantined event (M5 follow-up: operator-triggered) ────
        // Deletes the tracking row so the event's failure streak starts fresh (FailureCount back
        // to 0) — the same state transition ClearFailureAsync already performs automatically the
        // moment an event applies/pushes cleanly (SyncClient.cs), just triggered by an operator
        // instead of by success. Exists specifically so fixing the underlying cause (bad
        // signature, a peer's cap mismatch, whatever LastError pointed at) doesn't also require
        // editing the database by hand to give the event a fresh chance.
        //
        // IMPORTANT caveat, spelled out here because it's easy to assume this endpoint does more
        // than it does: clearing resets visibility/counters but does NOT itself force redelivery.
        // The pull/push cursor that skipped past this event when it was originally quarantined
        // (SyncClient.cs's lastApplied/pushAfter "advance past a quarantined event" logic) is left
        // untouched, so whether the event actually gets a chance to re-apply depends on it being
        // redelivered some other way — e.g. gossip relay from a different peer at a different
        // local sequence number, or a future rejoin/resnapshot. There is no general "rewind this
        // peer's position and re-fetch" tool today; building one is a materially bigger feature
        // (deciding which peer/sequence to rewind to, and re-validating everything applied after
        // that point) that's out of scope here — this endpoint only ever needed to stop requiring
        // manual DB surgery for the tracking row itself.
        app.MapDelete("/api/sync/quarantine/{eventId:guid}", async (Guid eventId, ISyncQuarantineRepository quarantineRepo) =>
        {
            await SyncEventQuarantine.ClearFailureAsync(quarantineRepo, eventId);
            return Results.NoContent();
        }).RequireInternalKey().WithTags("Sync");

        // ─── Reachability self-test: probe (local wizard call, internal-key-gated) ──
        // The originating node (running the internet-access wizard) calls THIS endpoint on
        // itself. It picks one active whitelisted peer, authenticates to it (reusing the same
        // challenge/sign/authenticate flow as SyncClient via PeerAuthenticator), and asks that
        // peer to relay-fetch the candidate URL — because a node can't prove its OWN public
        // reachability from inside its own LAN. See probe-relay below for the peer side.
        app.MapPost("/api/sync/probe", async (
            HttpContext ctx,
            IWhitelistRepository whitelistRepo,
            INodeIdentityRepository nodeRepo,
            INodeAuthSigner authSigner,
            System.Net.Http.IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory) =>
        {
            if (!CallerIdentity.Extract(ctx).IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var logger = loggerFactory.CreateLogger("SyncProbe");

            SyncProbeRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<SyncProbeRequest>(); }
            catch { return Results.BadRequest(new ErrorResponse("Invalid JSON")); }
            if (req == null || string.IsNullOrWhiteSpace(req.Url))
                return Results.Json(new SyncProbeResponse(
                    SyncProbeOutcome.InvalidUrl, null, null, null, SyncProbeErrorCategory.None,
                    "No URL supplied."), statusCode: 400);

            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.Json(new SyncProbeResponse(
                    SyncProbeOutcome.InvalidUrl, null, null, null, SyncProbeErrorCategory.None,
                    "Invalid URL — must be an absolute http(s) URL."), statusCode: 400);
            }
            var targetUrl = uri.ToString().TrimEnd('/');

            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return Results.Problem("Node is not initialized.", statusCode: 503);

            // Pick active, reachable whitelisted peers — same filter convention as
            // /api/sync/status, /delivery-status, etc. (Status == "A" && !empty ApiAddress).
            var whitelist = await whitelistRepo.GetAllActiveAsync();
            var peers = whitelist
                .Where(p => p.Status == "A" && !string.IsNullOrEmpty(p.ApiAddress))
                .ToList();

            if (peers.Count == 0)
            {
                return Results.Ok(new SyncProbeResponse(
                    SyncProbeOutcome.NoPeersAvailable,
                    null, null, null, SyncProbeErrorCategory.None,
                    "No whitelisted peers with a known address are available to verify reachability. " +
                    "Try opening this URL on your phone over mobile data (disconnect from Wi-Fi) to check manually."));
            }

            // Try peers in order until one successfully relays. A peer that is offline or
            // refuses auth is skipped (the result still needs to come from AT LEAST one
            // independent outside observer to be meaningful).
            var http = httpClientFactory.CreateClient();
            WhitelistEntry? usedPeer = null;
            SyncProbeRelayResponse? relayResult = null;
            foreach (var peer in peers)
            {
                var peerBase = peer.ApiAddress!.TrimEnd('/');
                string token;
                try
                {
                    // peer.NodeId (the whitelist entry we're iterating) is the trusted audience
                    // anchor for M6's challenge-relay protection — it's what we independently
                    // believe this peerBase belongs to, not anything the peer's own responses claim.
                    token = await PeerAuthenticator.AuthenticateAsync(
                        authSigner, http, peerBase, identity, peer.NodeId, ctx.RequestAborted);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Probe: could not authenticate to peer {Peer} ({Base})",
                        peer.DisplayName, peerBase);
                    continue; // try next peer
                }

                // Ask the peer to relay-fetch the target URL.
                try
                {
                    using var relayReq = new HttpRequestMessage(HttpMethod.Post, $"{peerBase}/api/sync/probe-relay");
                    relayReq.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    relayReq.Content = JsonContent.Create(new SyncProbeRelayRequest(targetUrl));
                    var relayResp = await http.SendAsync(relayReq, ctx.RequestAborted);
                    relayResp.EnsureSuccessStatusCode();
                    relayResult = await relayResp.Content.ReadFromJsonAsync<SyncProbeRelayResponse>(PeerJsonOpts)
                        ?? throw new InvalidDataException("Invalid relay response.");
                    usedPeer = peer;
                    break; // got a real relay result from this peer
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Probe: relay call to peer {Peer} ({Base}) failed",
                        peer.DisplayName, peerBase);
                    continue; // try next peer
                }
            }

            if (relayResult == null)
            {
                // No peer could be reached/authenticated at all.
                return Results.Ok(new SyncProbeResponse(
                    SyncProbeOutcome.PeerUnreachable,
                    null, null, null, SyncProbeErrorCategory.Unknown,
                    "Could not reach any whitelisted peer to perform the check. Verify your peers are online."));
            }

            return Results.Ok(new SyncProbeResponse(
                relayResult.Reachable ? SyncProbeOutcome.Reachable : SyncProbeOutcome.Unreachable,
                usedPeer!.NodeId,
                usedPeer.DisplayName,
                relayResult.HttpStatusCode,
                relayResult.ErrorCategory,
                relayResult.Reachable
                    ? $"Peer '{usedPeer.DisplayName}' confirmed {targetUrl} is reachable (HTTP {relayResult.HttpStatusCode})."
                    : $"Peer '{usedPeer.DisplayName}' could not reach {targetUrl} " +
                      $"({relayResult.ErrorCategory}). No response came back at all — this typically " +
                      $"means the port is not forwarded, or your ISP uses CGNAT."));
        }).RequireInternalKey().WithTags("Sync");

        // ─── Reachability self-test: relay (peer-to-peer, Bearer auth) ──────────
        // Called BY a whitelisted peer (standard TryAuth Bearer token), this node fetches
        // {url}/api/sync/ping and reports whether it got any HTTP response back. ANY HTTP
        // status (even the 403 from the ping endpoint's internal-key gate, or 503 invisible
        // mode) proves the target server is reachable from outside its LAN — i.e. the port
        // forward works. Only a total connection failure (refused/timeout/DNS) means
        // "unreachable", which is the signal that lets the wizard suggest CGNAT.
        app.MapPost("/api/sync/probe-relay", async (
            HttpContext ctx,
            SyncTokenStore store,
            System.Net.Http.IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IPublicHostValidator hostValidator) =>
        {
            if (!TryAuth(ctx, store, out _)) return Results.Unauthorized();

            SyncProbeRelayRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<SyncProbeRelayRequest>(); }
            catch { return Results.BadRequest(new ErrorResponse("Invalid JSON")); }
            if (req == null || string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest(new ErrorResponse("No URL supplied."));

            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Results.BadRequest(new ErrorResponse("Invalid URL — must be an absolute http(s) URL."));

            var logger = loggerFactory.CreateLogger("SyncProbeRelay");

            // SSRF guard: a whitelisted peer's whole purpose here is "fetch this PUBLIC URL for
            // me" — it must not become a way for a peer to make this node probe its own loopback,
            // LAN, or cloud-metadata addresses. Reject any target that resolves to a
            // loopback/private/link-local/unspecified address.
            if (!await hostValidator.IsPublicHostAsync(uri.Host, ctx.RequestAborted))
                return Results.BadRequest(new ErrorResponse(
                    "Target host must resolve to a public address (not loopback/private/link-local)."));

            var target = $"{uri.ToString().TrimEnd('/')}/api/sync/ping";

            // Bound the reachability check so a stealth-dropping firewall (silent packet drop,
            // typical CGNAT symptom) doesn't hang the wizard indefinitely.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var http = httpClientFactory.CreateClient();
            try
            {
                using var resp = await http.GetAsync(target, cts.Token);
                return Results.Ok(new SyncProbeRelayResponse(
                    Reachable: true,
                    HttpStatusCode: (int)resp.StatusCode,
                    ErrorCategory: SyncProbeErrorCategory.None,
                    ErrorDetail: null));
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Probe-relay: target {Url} timed out", target);
                return Results.Ok(new SyncProbeRelayResponse(
                    Reachable: false,
                    HttpStatusCode: null,
                    ErrorCategory: SyncProbeErrorCategory.Timeout,
                    ErrorDetail: "Connection timed out."));
            }
            catch (HttpRequestException ex)
            {
                var category = ClassifyHttpError(ex);
                logger.LogInformation("Probe-relay: target {Url} unreachable ({Category}): {Msg}",
                    target, category, ex.Message);
                return Results.Ok(new SyncProbeRelayResponse(
                    Reachable: false,
                    HttpStatusCode: null,
                    ErrorCategory: category,
                    ErrorDetail: ex.Message));
            }
        }).WithTags("Sync");
    }

    private static bool TryAuth(HttpContext ctx, SyncTokenStore store, out Guid nodeId)
    {
        nodeId = default;
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;
        var token = authHeader["Bearer ".Length..];
        return store.TryValidateToken(token, out nodeId);
    }

    /// <summary>
    /// Maps a connection-level <see cref="HttpRequestException"/> to a coarse category so the
    /// probe response can tell the wizard "no response came back at all" (CGNAT candidate) apart
    /// from e.g. a TLS misconfiguration. Intentionally coarse-grained — exact socket errors vary
    /// by platform/runtime, and the wizard only needs the broad distinction.
    /// </summary>
    private static SyncProbeErrorCategory ClassifyHttpError(HttpRequestException ex)
    {
        if (ex.InnerException is System.Net.Sockets.SocketException sock)
        {
            return sock.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => SyncProbeErrorCategory.ConnectionRefused,
                System.Net.Sockets.SocketError.TimedOut => SyncProbeErrorCategory.Timeout,
                System.Net.Sockets.SocketError.HostNotFound or
                System.Net.Sockets.SocketError.HostUnreachable or
                System.Net.Sockets.SocketError.HostDown => SyncProbeErrorCategory.DnsFailure,
                _ => SyncProbeErrorCategory.Unknown,
            };
        }

        // Authentication/cert failures surface as HttpRequestException with an SSL/TLS message
        // (no socket inner exception on some runtimes).
        var msg = ex.Message.AsSpan();
        if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return SyncProbeErrorCategory.TlsError;
        }

        return SyncProbeErrorCategory.Unknown;
    }

    /// <summary>
    /// Truncate an IP for logs. v4: last octet → "x" (1.2.3.x). v6: last 80 bits → "::x".
    /// Preserves enough resolution for abuse-pattern detection while reducing PII surface.
    /// </summary>
    private static string MaskIp(System.Net.IPAddress? ip)
    {
        if (ip == null) return "?";
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x";
        if (bytes.Length == 16)
        {
            // First 48 bits (network prefix) preserved; rest zeroed for display.
            return $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}::x";
        }
        return "?";
    }
}

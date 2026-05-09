using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

public static class SyncEndpoints
{
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
                Convert.ToBase64String(identity.Ed25519PublicKey)));
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
            SyncTokenStore store,
            INodeIdentityRepository nodeRepo) =>
        {
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

            if (!store.ConsumeChallenge(req.ChallengeB64, out _))
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

            // Domain-separated Ed25519 signature: prepend "BMB-CHALLENGE-V1\0" tag before signing
            // to prevent cross-protocol oracle attacks. All clients in this repo use the tagged form.
            var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
            var taggedPayload = domainTag.Concat(challengeBytes).ToArray();
            var sigOk = Ed25519Signer.Verify(entry.Ed25519PublicKey, taggedPayload, signature);
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

            if (ctx.Request.ContentLength is > 10 * 1024 * 1024)
                return Results.StatusCode(413);

            SyncEvent[] events;
            try
            {
                events = await ctx.Request.ReadFromJsonAsync<SyncEvent[]>() ?? [];
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
            INodeIdentityRepository nodeRepo) =>
        {
            if (!BeeMemoryBank.Api.Middleware.InternalKeyValidator.Validate(ctx))
                return Results.Json(new BeeMemoryBank.Api.Models.ErrorResponse("Unauthorized"), statusCode: 403);
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
                nodes = nodeStatuses
            });
        }).WithTags("Sync");

        // ─── Ping (lightweight check if new events exist) ────────────────────────
        app.MapGet("/api/sync/ping", async (
            HttpContext ctx,
            IEventLogRepository eventLogRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            long afterSequence = 0) =>
        {
            if (!BeeMemoryBank.Api.Middleware.InternalKeyValidator.Validate(ctx))
                return Results.Json(new BeeMemoryBank.Api.Models.ErrorResponse("Unauthorized"), statusCode: 403);
            if (invisibleMode.IsInvisible) return Results.StatusCode(503);
            var events = await eventLogRepo.GetAfterSequenceAsync(afterSequence, 1);
            if (events.Count == 0)
                return Results.NoContent();
            return Results.Ok(new { count = await eventLogRepo.GetTotalCountAsync() - afterSequence });
        }).WithTags("Sync");

        app.MapGet("/api/sync/invisible", (
            HttpContext ctx,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode) =>
        {
            if (!BeeMemoryBank.Api.Middleware.InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);
            return Results.Ok(new { IsInvisible = invisibleMode.IsInvisible });
        }).WithTags("Sync");

        // ─── Invisible Mode Toggle ───────────────────────────────────────────────
        app.MapPost("/api/sync/invisible", (
            HttpContext ctx,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            [Microsoft.AspNetCore.Mvc.FromBody] bool isInvisible) =>
        {
            if (!BeeMemoryBank.Api.Middleware.InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            invisibleMode.IsInvisible = isInvisible;
            return Results.Ok();
        }).WithTags("Sync");

        // ─── Delivery status (requires internal key — exposes node topology) ────
        app.MapGet("/api/sync/delivery-status", async (
            HttpContext ctx,
            IEventLogRepository eventLogRepo,
            ISyncPushPositionRepository pushPositionRepo,
            IWhitelistRepository whitelistRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            INodeIdentityRepository nodeRepo) =>
        {
            if (!BeeMemoryBank.Api.Middleware.InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var identity = await nodeRepo.GetAsync();
            var totalEvents = await eventLogRepo.GetTotalCountAsync();
            var pushPositions = await pushPositionRepo.GetAllAsync();
            var whitelist = await whitelistRepo.GetAllActiveAsync();

            var statuses = whitelist
                .Where(node => node.NodeId != identity?.NodeId) // exclude self
                .Select(node =>
            {
                var push = pushPositions.FirstOrDefault(p => p.RemoteNodeId == node.NodeId);
                var nodeType = string.IsNullOrEmpty(node.ApiAddress) ? "private" : "public";
                return new DeliveryNodeStatus(
                    node.NodeId,
                    node.DisplayName,
                    nodeType,
                    push?.LastPushedSeq ?? 0,
                    totalEvents,
                    (push?.LastPushedSeq ?? 0) >= totalEvents,
                    push?.PushedAt);
            }).ToList();

            return Results.Ok(new DeliveryStatusResponse(identity?.NodeId, invisibleMode.IsInvisible, statuses));
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

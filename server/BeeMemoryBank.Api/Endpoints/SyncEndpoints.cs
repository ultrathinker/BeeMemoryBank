using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
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
            IWhitelistRepository whitelist) =>
        {
            // Verify challenge
            if (!store.ConsumeChallenge(req.ChallengeB64, out _))
                return Results.Unauthorized();

            // Find the node in the whitelist
            var entry = await whitelist.GetByNodeIdAsync(req.NodeId);
            if (entry == null || entry.Status != "A")
                return Results.Unauthorized();

            // Verify signature
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

            if (!Ed25519Signer.Verify(entry.Ed25519PublicKey, challengeBytes, signature))
                return Results.Unauthorized();

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
            var events = await eventLogRepo.GetAfterSequenceAsync(afterSequence, limit);

            // Record the highest sequence we sent, so delivery-status knows this node is up to date
            if (events.Count > 0)
                await pushPositionRepo.UpdatePositionAsync(nodeId, events[^1].SequenceNum);

            return Results.Ok(events);
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
            SyncEvent[] events,
            EventApplier applier,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            ILoggerFactory loggerFactory) =>
        {
            if (!TryAuth(ctx, store, out _)) return Results.Unauthorized();
            if (invisibleMode.IsInvisible) return Results.StatusCode(503);

            var logger = loggerFactory.CreateLogger("SyncEndpoints");
            int applied = 0, skipped = 0;
            foreach (var evt in events)
            {
                try
                {
                    await applier.ApplyAsync(evt);
                    applied++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipped event {EventId} of type {EventType}", evt.EventId, evt.EventType);
                    skipped++;
                }
            }
            return Results.Ok(new SyncApplyResult(applied, skipped));
        }).WithTags("Sync");

        // ─── Sync status (for Web UI progress display) ─────────────────────────
        app.MapGet("/api/sync/status", async (
            IEventLogRepository eventLogRepo,
            ISyncPositionRepository syncPositionRepo,
            IWhitelistRepository whitelistRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            INodeIdentityRepository nodeRepo) =>
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
                nodes = nodeStatuses
            });
        }).WithTags("Sync");

        // ─── Ping (lightweight check if new events exist) ────────────────────────
        app.MapGet("/api/sync/ping", async (
            IEventLogRepository eventLogRepo,
            BeeMemoryBank.Core.Services.InvisibleModeService invisibleMode,
            long afterSequence = 0) =>
        {
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
            if (role != "superadmin")
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
            if (role != "superadmin")
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
}

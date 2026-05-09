using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;

namespace BeeMemoryBank.Api.Endpoints;

public static class DekRotationEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapDekRotationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dek-rotation").WithTags("DekRotation");

        group.MapPost("/propose", async (
            ProposeDekRotationRequest req,
            DekRotationService svc,
            SessionService session,
            IAuditLogRepository auditRepo,
            HttpContext ctx,
            ILogger<Program> logger) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                int? initiatorUserId = null;
                if (int.TryParse(ctx.Request.Headers["X-User-Id"].FirstOrDefault(), out var uid))
                    initiatorUserId = uid;

                var commitEventId = await svc.ProposeRotationAsync(req.MasterPassword, initiatorUserId);

                var actorId = initiatorUserId?.ToString() ?? "system";
                await auditRepo.LogAsync(
                    "dek_rotation",
                    commitEventId.ToString(),
                    "dek_rotation_proposed",
                    "web",
                    $"DEK rotation proposed by user {actorId}");

                return Results.Ok(new { commitEventId });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 403);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("in progress"))
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
        });

        // Roadmap p4: /accept returns 202 immediately and fire-and-forgets the destructive
        // work. For large vaults the rewrap loop takes minutes, longer than nginx/browser
        // timeouts. UI already polls /progress; password / lock / "already in progress"
        // errors surface as currentStep=Failed + errorMessage in the next poll.
        group.MapPost("/accept", (
            AcceptDekRotationRequest req,
            DekRotationService svc,
            SessionService session,
            HttpContext ctx,
            ILogger<Program> logger) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            int? initiatorUserId = null;
            if (int.TryParse(ctx.Request.Headers["X-User-Id"].FirstOrDefault(), out var uid))
                initiatorUserId = uid;

            _ = Task.Run(async () =>
            {
                try
                {
                    await svc.AcceptCommitAsync(req.CommitEventId, req.MasterPassword, initiatorUserId);
                }
                catch (Exception ex)
                {
                    // svc itself sets _currentStep=Failed and _errorMessage on its catch path,
                    // so the polling UI sees the failure. We just log here.
                    logger.LogError(ex, "Background DEK rotation accept failed for {CommitEventId}", req.CommitEventId);
                }
            });

            return Results.Accepted(value: new { commitEventId = req.CommitEventId });
        });

        group.MapPost("/cancel/{eventId}", async (
            string eventId,
            DekRotationService svc,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            try
            {
                await svc.CancelAsync(eventId);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        group.MapGet("/progress", (DekRotationService svc, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            return Results.Ok(svc.GetProgress());
        });

        // Same fire-and-forget treatment as /accept (roadmap p4) — auto-accept is also
        // potentially long-running on large vaults.
        group.MapPost("/peer-accept/{commitEventId}", async (
            string commitEventId,
            IDekRotationApplier applier,
            SessionService session,
            IEventLogRepository eventLogRepo,
            IAuditLogRepository auditRepo,
            HttpContext ctx,
            ILogger<Program> logger) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var raw = await eventLogRepo.GetByIdAsync(commitEventId);

            if (raw == null)
                return Results.Json(new ErrorResponse($"Commit event {commitEventId} not found"), statusCode: 404);
            if (raw.EventType != EventTypes.DekRotationCommit)
                return Results.Json(new ErrorResponse($"Event {commitEventId} is not a dek_rotation_commit"), statusCode: 400);
            var commitEvent = raw;

            int? userId = null;
            if (int.TryParse(ctx.Request.Headers["X-User-Id"].FirstOrDefault(), out var uid))
                userId = uid;

            await auditRepo.LogAsync(
                "dek_rotation",
                commitEventId,
                "dek_rotation_peer_accepted",
                "web",
                $"Peer DEK rotation manually accepted by user {userId?.ToString() ?? "system"}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await applier.AutoAcceptCommitAsync(commitEvent);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background peer DEK rotation accept failed for {CommitEventId}", commitEventId);
                }
            });

            return Results.Accepted(value: new { commitEventId });
        });

        group.MapPost("/peer-reject/{commitEventId}", async (
            string commitEventId,
            IDekRotationStateRepository stateRepo,
            IAuditLogRepository auditRepo,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            await stateRepo.UpdateStateAsync(commitEventId, DekRotationState.Rejected, "Rejected by peer admin");

            int? userId = null;
            if (int.TryParse(ctx.Request.Headers["X-User-Id"].FirstOrDefault(), out var uid))
                userId = uid;

            await auditRepo.LogAsync(
                "dek_rotation",
                commitEventId,
                "dek_rotation_peer_rejected",
                "web",
                $"Peer DEK rotation rejected by user {userId?.ToString() ?? "system"}; node will desync from network if peers proceed without it");

            return Results.Ok();
        });

        group.MapGet("/peer-pending", async (
            DbConnectionFactory connFactory,
            IEventLogRepository eventLogRepo,
            INodeIdentityRepository nodeIdentityRepo,
            IWhitelistRepository whitelistRepo,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var identity = await nodeIdentityRepo.GetAsync();
            if (identity == null)
                return Results.Ok(new List<object>());

            var localNodeId = identity.NodeId.ToString();

            using var conn = connFactory.CreateConnection();
            var committingRows = await conn.QueryAsync<dynamic>(
                @"SELECT drs.event_id AS EventId, drs.rotation_ts AS RotationTs
                  FROM tbl_dek_rotation_state drs
                  WHERE drs.state = 'COMMITTING'");

            var result = new List<PeerPendingDekRotationDto>();

            foreach (var row in committingRows)
            {
                var eventId = (string)row.EventId;
                var rotationTs = (string)row.RotationTs;

                var evt = await eventLogRepo.GetByIdAsync(eventId);

                if (evt == null || evt.EventType != EventTypes.DekRotationCommit)
                    continue;

                DekRotationCommitPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<DekRotationCommitPayload>(evt.Payload, JsonOpts);
                }
                catch
                {
                    continue;
                }
                if (payload == null) continue;

                if (payload.OriginatorNodeId.Equals(localNodeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Guid.TryParse(payload.OriginatorNodeId, out var originatorGuid))
                    continue;
                var originator = await whitelistRepo.GetByNodeIdAsync(originatorGuid);
                var displayName = originator?.DisplayName ?? payload.OriginatorNodeId;

                result.Add(new PeerPendingDekRotationDto(eventId, payload.OriginatorNodeId, displayName, rotationTs));
            }

            return Results.Ok(result);
        });
    }
}

public record PeerPendingDekRotationDto(
    string EventId,
    string OriginatorNodeId,
    string OriginatorDisplayName,
    string RotationTs);


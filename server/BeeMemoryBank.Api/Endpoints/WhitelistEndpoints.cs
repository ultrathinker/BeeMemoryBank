using System.Net.Http.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

file record RemoteIdentityCheck(Guid NodeId);
file record SyncStatusEntry(Guid NodeId, DateTime UpdatedAt);

public static class WhitelistEndpoints
{
    public static void MapWhitelistEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/whitelist").WithTags("Whitelist");

        group.MapGet("/sync-status", async (HttpContext ctx, ISyncPositionRepository syncRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var positions = await syncRepo.GetAllAsync();
            return Results.Ok(positions.Select(p => new SyncStatusEntry(p.RemoteNodeId, p.UpdatedAt)));
        });

        // GET /api/whitelist — list active entries (no unlock required)
        group.MapGet("/", async (HttpContext ctx, IWhitelistRepository repo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var entries = await repo.GetAllActiveAsync();
            return Results.Ok(entries.Select(WhitelistEntryResponse.From));
        });

        // GET /api/whitelist/{nodeId} — single entry
        group.MapGet("/{nodeId:guid}", async (Guid nodeId, HttpContext ctx, IWhitelistRepository repo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            return entry != null
                ? Results.Ok(WhitelistEntryResponse.From(entry))
                : Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));
        });

        // PUT /api/whitelist/{nodeId} — update ApiAddress and CanGenerateEmbeddings
        group.MapPut("/{nodeId:guid}", async (
            Guid nodeId,
            UpdateWhitelistEntryRequest req,
            IWhitelistRepository repo,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            if (req.DisplayName != null) entry.DisplayName = req.DisplayName;
            if (req.ApiAddress != null) entry.ApiAddress = req.ApiAddress;
            if (req.CanGenerateEmbeddings.HasValue) entry.CanGenerateEmbeddings = req.CanGenerateEmbeddings.Value;
            entry.UpdatedAt = DateTime.UtcNow;

            await repo.UpdateAsync(entry);
            return Results.Ok(WhitelistEntryResponse.From(entry));
        });

        // PUT /api/whitelist/{nodeId}/address — change node URL with validation
        group.MapPut("/{nodeId:guid}/address", async (
            Guid nodeId,
            ChangeNodeAddressRequest req,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            INodeIdentityRepository nodeIdentityRepo,
            IHttpClientFactory httpClientFactory,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            // 1. Verify master password
            if (!session.IsUnlocked)
            {
                var unlocked = await session.UnlockAsync(req.Password);
                if (!unlocked)
                    return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);
            }

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            // 2. Cannot change your own URL
            var localIdentity = await nodeIdentityRepo.GetAsync();
            if (localIdentity != null && localIdentity.NodeId == nodeId)
                return Results.BadRequest(new ErrorResponse("Cannot change the URL of your own node"));

            // 3. Verify that the new URL responds and belongs to the same node
            var newUrl = req.NewApiAddress.Trim().TrimEnd('/');
            if (!newUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !newUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                newUrl = "https://" + newUrl;
            try
            {
                var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                var resp = await http.GetAsync($"{newUrl}/api/sync/identity");
                resp.EnsureSuccessStatusCode();
                var identity = await resp.Content.ReadFromJsonAsync<RemoteIdentityCheck>();
                if (identity == null)
                    return Results.BadRequest(new ErrorResponse("Failed to get identity from the remote node"));
                if (identity.NodeId != nodeId)
                    return Results.BadRequest(new ErrorResponse(
                        $"NodeId at the new URL ({identity.NodeId}) does not match the expected ({nodeId}). This is a different node!"));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to connect to {newUrl}: {ex.Message}"));
            }

            // 4. Verify that the URL is not already used by another node
            var allEntries = await repo.GetAllActiveAsync();
            var conflict = allEntries.FirstOrDefault(e => e.NodeId != nodeId
                && string.Equals(e.ApiAddress, newUrl, StringComparison.OrdinalIgnoreCase));
            if (conflict != null)
                return Results.BadRequest(new ErrorResponse($"URL is already used by node {conflict.DisplayName}"));

            // 5. Update
            entry.ApiAddress = newUrl;
            entry.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(entry);

            // 6. Sync event
            await eventLogger.LogWhitelistUpdateAsync(nodeId, newUrl, null);

            return Results.Ok(WhitelistEntryResponse.From(entry));
        });

        // PUT /api/whitelist/{nodeId}/auto-accept-restore — toggle auto-accept restore
        group.MapPut("/{nodeId:guid}/auto-accept-restore", async (
            Guid nodeId,
            SetAutoAcceptRestoreRequest req,
            IWhitelistRepository repo,
            SessionService session,
            INodeIdentityRepository nodeIdentityRepo,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            var localIdentity = await nodeIdentityRepo.GetAsync();
            if (localIdentity != null && localIdentity.NodeId == nodeId)
                return Results.BadRequest(new ErrorResponse("Cannot set auto-accept for the local node"));

            await repo.SetAutoAcceptRestoreAsync(nodeId.ToString(), req.AutoAccept);
            return Results.Ok(new { success = true, autoAccept = req.AutoAccept });
        });

        // PUT /api/whitelist/{nodeId}/auto-accept-dek-rotation — toggle auto-accept DEK rotation
        group.MapPut("/{nodeId:guid}/auto-accept-dek-rotation", async (
            Guid nodeId,
            SetAutoAcceptDekRotationRequest req,
            IWhitelistRepository repo,
            SessionService session,
            INodeIdentityRepository nodeIdentityRepo,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            var localIdentity = await nodeIdentityRepo.GetAsync();
            if (localIdentity != null && localIdentity.NodeId == nodeId)
                return Results.BadRequest(new ErrorResponse("Cannot set auto-accept for the local node"));

            await repo.SetAutoAcceptDekRotationAsync(nodeId.ToString(), req.AutoAccept);
            return Results.Ok(new { success = true, autoAccept = req.AutoAccept });
        });

        // DELETE /api/whitelist/{nodeId} — revoke access (requires unlock)
        group.MapDelete("/{nodeId:guid}", async (
            Guid nodeId,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            await repo.RevokeAsync(nodeId);
            await eventLogger.LogWhitelistRevokeAsync(nodeId);

            return Results.NoContent();
        });
    }
}

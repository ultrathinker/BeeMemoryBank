using System.Net.Http.Json;
using BeeMemoryBank.Api.Helpers;
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
        // Superadmin for the WHOLE group, reads included.
        //
        // The five mutating routes asserted that first (only the two auto-accept toggles did
        // before the endpoint-filter sweep; editing a peer's stored URL and revoking a peer
        // outright were reachable by ANY signed-in user with an unlocked session, and both
        // reshape this node's sync topology).
        //
        // The three GETs followed. Their old comment claimed "the Nodes page renders them for
        // everyone" — there is no Nodes page: the only renderer is Admin.cshtml, which is
        // [Authorize(Roles = "superadmin")], and the only other caller is that page's own
        // sync-status poll. So no ordinary user ever saw this data through the UI, while the API
        // handed it to any of them who asked: every peer's node id, display name and — the part
        // that matters — its api_address, an internal URL of someone else's machine. That is a
        // map of the private network this node syncs with, and it is exactly what an attacker who
        // has compromised one low-privilege account wants next. Key material was never in here,
        // but "no key material" was the wrong test.
        var group = app.MapGroup("/api/whitelist").WithTags("Whitelist")
            .RequireInternalKey().RequireSuperadmin();

        group.MapGet("/sync-status", async (HttpContext ctx, ISyncPositionRepository syncRepo, ISyncPushPositionRepository pushRepo) =>
        {

            // "Last sync" = most recent contact in EITHER direction. sync_position tracks how far
            // WE pulled FROM a node; push_position tracks how far a node pulled FROM us / reported.
            // Private nodes (no API address) are never pulled from, so their only signal is the
            // push position — using sync_position alone left them at DateTime.MinValue (rendered
            // as a nonsensical "739772d ago").
            var latest = new Dictionary<Guid, DateTime>();
            foreach (var p in await syncRepo.GetAllAsync())
                if (!latest.TryGetValue(p.RemoteNodeId, out var cur) || p.UpdatedAt > cur)
                    latest[p.RemoteNodeId] = p.UpdatedAt;
            foreach (var p in await pushRepo.GetAllAsync())
                if (!latest.TryGetValue(p.RemoteNodeId, out var cur) || p.PushedAt > cur)
                    latest[p.RemoteNodeId] = p.PushedAt;

            return Results.Ok(latest.Select(kv => new SyncStatusEntry(kv.Key, kv.Value)));
        });

        // GET /api/whitelist — list active entries (no unlock required)
        group.MapGet("/", async (HttpContext ctx, IWhitelistRepository repo) =>
        {
            var entries = await repo.GetAllActiveAsync();
            return Results.Ok(entries.Select(WhitelistEntryResponse.From));
        });

        // GET /api/whitelist/{nodeId} — single entry
        group.MapGet("/{nodeId:guid}", async (Guid nodeId, HttpContext ctx, IWhitelistRepository repo) =>
        {
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
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            if (req.DisplayName != null) entry.DisplayName = req.DisplayName;
            // Normalize the same way /api/join and the /address endpoint below do: every consumer
            // builds request URLs as $"{apiAddress}/api/sync/...", so a stored trailing slash
            // yields a double slash and a 404. The sync clients trim defensively too, but the
            // stored value also travels to other nodes, so keep the table itself clean.
            if (req.ApiAddress != null) entry.ApiAddress = req.ApiAddress.Trim().TrimEnd('/');
            if (req.CanGenerateEmbeddings.HasValue) entry.CanGenerateEmbeddings = req.CanGenerateEmbeddings.Value;
            entry.UpdatedAt = DateTime.UtcNow;

            await repo.UpdateAsync(entry);
            return Results.Ok(WhitelistEntryResponse.From(entry));
        });

        // PUT /api/whitelist/{nodeId}/superadmin — promote or demote a peer
        group.MapPut("/{nodeId:guid}/superadmin", async (
            Guid nodeId,
            SetPeerSuperadminRequest req,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            IAuditLogRepository auditRepo,
            HttpContext ctx) =>
        {
            // Every node that joins with the master password arrives as a superadmin, because a
            // join grants full trust — and until now there was no way back. A peer you stopped
            // trusting could only be revoked outright, which also stops it receiving content; there
            // was nothing between "full member of the mesh" and "cut off". This is that step.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            if (entry.IsSuperadmin == req.IsSuperadmin)
                return Results.Ok(WhitelistEntryResponse.From(entry));

            entry.IsSuperadmin = req.IsSuperadmin;
            entry.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(entry);

            // Tell the rest of the mesh. Each node enforces the flag from its OWN whitelist row —
            // EventApplier checks it before accepting hard_delete, restore_network and whitelist
            // changes — so a demotion that reaches only this node leaves the peer just as powerful
            // everywhere else. A node that is offline picks it up on catch-up.
            await eventLogger.LogWhitelistUpdateAsync(nodeId, apiAddress: null, displayName: null,
                isSuperadmin: req.IsSuperadmin);
            eventLogger.SignalSync();

            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await auditRepo.LogAsync("whitelist", nodeId.ToString(),
                req.IsSuperadmin ? "peer_promoted" : "peer_demoted", "web",
                $"Peer {entry.DisplayName} {(req.IsSuperadmin ? "promoted to" : "demoted from")} superadmin by user {actor}");

            return Results.Ok(WhitelistEntryResponse.From(entry));
        }).RequireSuperadmin().RequireNonAgent();

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

            // 1. Re-authenticate. The whitelist_update event below is signed with the node identity
            // key, which needs the master DEK — so the vault must already be open, like the sibling
            // endpoints. This used to unlock the shared session with the supplied password when
            // locked (and skip the password entirely when not), turning a URL edit into a hidden
            // unlock path. Now the password is a plain re-authentication in both states.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (!await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

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

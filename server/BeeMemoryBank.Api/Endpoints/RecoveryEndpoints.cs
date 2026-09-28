using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Api.Endpoints;

public static class RecoveryEndpoints
{
    public static void MapRecoveryEndpoints(this WebApplication app)
    {
        // Admin view of the blind-node recovery boxes (plan 5.6, 6.3, 6.5): how many, whether one
        // holds the current key, and which devices use a different password than the signed-in
        // superadmin. Nothing secret, but it describes the vault's ways in, hence superadmin only.
        var group = app.MapGroup("/api/recovery").WithTags("Recovery")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        // Restored peers no anchor vouched for (RestoredPeerStatus.Unconfirmed): listed, and made active again
        // one by one by a superadmin who has checked them. Local only; the superadmin flag stays cleared.
        group.MapGet("/restored-peers", async (IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<(string Id, string Name, string? Address)>(
                "SELECT node_id, display_name, api_address FROM tbl_whitelist WHERE status = @U", new { U = RestoredPeerStatus.Unconfirmed });
            return Results.Ok(rows.Where(r => Guid.TryParse(r.Id, out _))
                .Select(r => new { nodeId = Guid.Parse(r.Id), displayName = r.Name, apiAddress = r.Address }));
        });
        group.MapPost("/restored-peers/{nodeId:guid}/confirm", async (Guid nodeId, IDbConnectionFactory db, BeeMemoryBank.Sync.Blind.SpkiPinRegistry pins) =>
        {
            using var conn = db.CreateConnection();
            var changed = await conn.ExecuteAsync(
                "UPDATE tbl_whitelist SET status = 'A', updated_at = @Now WHERE node_id = @Id COLLATE NOCASE AND status = @U",
                new { Id = nodeId.ToString(), U = RestoredPeerStatus.Unconfirmed, Now = DateTime.UtcNow.ToString("O") });
            pins.Invalidate();
            return changed == 1 ? Results.NoContent() : Results.NotFound();
        });

        group.MapGet("/status", async (HttpContext ctx, RecoveryStatusService status, IUserRepository users) =>
        {
            int? slotId = null;
            if (CallerIdentity.Extract(ctx).UserId is { } userId)
                slotId = (await users.GetByIdAsync(userId))?.KeySlotId;
            return Results.Ok(await status.GetAsync(slotId));
        });
    }
}

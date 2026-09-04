using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class KeyEndpoints
{
    public static void MapKeyEndpoints(this WebApplication app)
    {
        // Master-key operations: /change-password re-wraps the master DEK, /add-recovery mints a
        // recovery key that opens the whole vault. Both used to be gated only by "internal key +
        // session unlocked" — any Web-layer caller, regardless of role, could have minted a vault
        // key; the Web happened to gate its proxy route as superadmin, which is the wrong layer to
        // rely on. Superadmin is now required here too.
        // RequireNonAgent as well as RequireSuperadmin: a superadmin's MCP agent inherits its
        // owner's IsSuperadmin flag (AgentAuthMiddleware builds the CallerIdentity that way, by
        // design — it is how an agent reads what its owner can read). Minting a recovery key or
        // re-wrapping the master DEK is not that kind of operation: it is a human, break-glass
        // action, and an agent key that leaked must not be able to mint a second key to the vault.
        // The internal-key gate already keeps agents out today — they present a bee_ bearer token
        // and no X-Internal-Key — but that is a property of the deployment, not of this rule.
        var group = app.MapGroup("/api/keys").WithTags("Keys")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        group.MapPost("/change-password", async (
            ChangePasswordRequest req,
            KeyManagementService svc,
            SessionService session,
            IEventLogger eventLogger,
            INodeIdentityRepository nodeRepo,
            IWhitelistRepository whitelistRepo,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            await svc.ChangePasswordAsync(req.OldPassword, req.NewPassword);

            // This node is now in step with itself again, whatever a peer told us earlier.
            await nodeRepo.ClearMasterPasswordNoticeAsync();

            // ...and remember WHEN, so a peer's event about an older change does not raise the
            // banner again. The SAME instant goes into the event below, not two calls to UtcNow:
            // the applier's guard is `incoming <= local`, so if our own event carried a later
            // timestamp than what we recorded, an echo of it coming back through a relay peer
            // would raise the banner on the very node that made the change.
            var changedAt = DateTime.UtcNow;
            await nodeRepo.SetMasterPasswordChangedLocallyAtAsync(changedAt);

            // Key slots are node-local: this rewrapped THIS node's slot and nothing else. Every
            // peer still accepts the old password, including at its own /api/join, which is how a
            // stranger becomes a full member of the mesh. Tell them, so each can say so to its own
            // admin — the event deliberately carries no key material, so the new password has to be
            // typed on each node by a human.
            var peers = await whitelistRepo.GetAllActiveAsync();
            if (peers.Count > 0)
            {
                await eventLogger.LogMasterPasswordChangedAsync(changedAt);
                eventLogger.SignalSync();
            }

            return Results.Ok(new ChangePasswordResponse(
                PeerCount: peers.Count,
                Message: peers.Count == 0
                    ? "Master password changed."
                    : $"Master password changed on this node only. {peers.Count} other node(s) still " +
                      "accept the OLD password, including at their own join endpoint — change it on each " +
                      "of them as well. Note that changing the password does not evict a node that already " +
                      "holds the master key: that needs revoking the peer and rotating the DEK."));
        });

        // GET /api/keys/password-notice — has the password been changed on another node?
        group.MapGet("/password-notice", async (INodeIdentityRepository nodeRepo) =>
        {
            // Read separately from the change endpoint because it answers a question about the
            // PAST: a peer changed the password while this node was running or offline, and this
            // node still accepts the old one. It is cleared by changing the password here, or by
            // an admin dismissing it below.
            var notice = await nodeRepo.GetMasterPasswordNoticeAsync();
            return Results.Ok(notice is null
                ? new MasterPasswordNoticeResponse(false, null, null)
                : new MasterPasswordNoticeResponse(true, notice.Value.ChangedAt, notice.Value.ByNode));
        });

        // POST /api/keys/password-notice/dismiss — "I have handled this."
        //
        // The notice exists because a peer announced a password change and this node cannot tell
        // whether that change matches its own. Migration 019 filters the half that IS decidable
        // (an announcement older than this node's own last change). The other half is not: when a
        // peer changes AFTER this node did, the event says nothing about WHICH password it moved
        // to, so "the admin is working through the mesh, and this is node 1 hearing about node 3"
        // and "someone changed the password on a machine I do not control" are the same event.
        //
        // Only the operator knows which it was, so they say so. Without this the warning was
        // permanent for every node but the last one changed — following the exact procedure the
        // banner prescribes left it on screen forever, which is how a real warning becomes
        // furniture nobody reads.
        group.MapPost("/password-notice/dismiss", async (INodeIdentityRepository nodeRepo) =>
        {
            await nodeRepo.ClearMasterPasswordNoticeAsync();
            return Results.Ok(new MasterPasswordNoticeResponse(false, null, null));
        });

        group.MapPost("/add-recovery", async (KeyManagementService svc, SessionService session, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var key = await svc.AddRecoveryKeyAsync();
            return Results.Ok(new RecoveryKeyResponse(key));
        });
    }
}

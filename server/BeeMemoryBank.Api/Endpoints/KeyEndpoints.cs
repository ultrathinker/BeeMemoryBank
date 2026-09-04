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

        group.MapPost("/change-password", async (ChangePasswordRequest req, KeyManagementService svc, SessionService session, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            await svc.ChangePasswordAsync(req.OldPassword, req.NewPassword);
            return Results.Ok();
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

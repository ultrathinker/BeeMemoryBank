using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Vault.Metadata;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// The metadata activation of Step A (docs/design/step-a-spec.md 5.3, 5.4): the readiness report and the one-way switch.
/// Superadmin, never an agent; the switch also asks for the master password (re-authentication, never an unlock).
/// A full node only: a blind node has nothing to activate and does not map these routes.
/// </summary>
public static class SyncActivationEndpoints
{
    public sealed record ActivationRequest(string? Password);

    public static void MapSyncActivationEndpoints(this WebApplication app)
    {
        // peers=false: this node's own observations only, without calling anybody (the Admin page's first look).
        app.MapGet("/api/sync/readiness", async (MetadataReadinessService readiness, bool? peers, CancellationToken ct) =>
            Results.Ok(await readiness.GetReportAsync(askPeers: peers ?? true, ct)))
            .RequireSuperadmin().RequireNonAgent().WithTags("Sync");

        app.MapPost("/api/sync/activation", async (
            ActivationRequest req,
            SessionService session,
            MetadataActivationService activation,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("The vault is locked; unlock it first."), statusCode: StatusCodes.Status423Locked);
            if (string.IsNullOrEmpty(req.Password) || !await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: StatusCodes.Status401Unauthorized);

            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            var outcome = await activation.ActivateAsync(actor, ct);
            if (!outcome.Activated)
                return Results.Json(new { blockers = outcome.Blockers }, statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(new
            {
                activation_id = outcome.ActivationId,
                key_id = outcome.KeyId,
                generation = outcome.Generation,
            });
        }).RequireSuperadmin().RequireNonAgent().WithTags("Sync");
    }
}

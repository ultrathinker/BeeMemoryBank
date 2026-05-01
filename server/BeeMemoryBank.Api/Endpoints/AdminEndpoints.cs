using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin");

        // backfill-media-links was a one-shot migration helper for an earlier schema change.
        // Disabled: leaving the rest of the /api/admin group registered so concept-tag-edge
        // stats + rebuild work. If we ever need backfill again, restore from git history.

        group.MapGet("/concept-tag-edge/stats", async (
            HttpContext ctx, SessionService session, IConceptTagRepository conceptTagRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var caller = CallerIdentity.Extract(ctx);
            if (!caller.IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var stats = await conceptTagRepo.GetEdgeStatsAsync();
            return Results.Ok(stats);
        });

        group.MapPost("/concept-tag-edge/rebuild", async (
            HttpContext ctx, SessionService session, IConceptTagRepository conceptTagRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var caller = CallerIdentity.Extract(ctx);
            if (!caller.IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var report = await conceptTagRepo.CheckAndRebuildEdgesAsync();
            return Results.Ok(report);
        }).DisableAntiforgery();
    }
}

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

public static class HardDeleteEndpoints
{
    public static void MapHardDeleteEndpoints(this WebApplication app)
    {
        // Purging content past the soft-delete tombstone is superadmin-only across the board.
        // Note the ORDER change this makes visible: the filter answers "wrong role" before any
        // handler runs, so a locked session no longer masks the 403 with "Session is locked".
        var group = app.MapGroup("/api/hard-delete").WithTags("HardDelete")
            .RequireInternalKey().RequireSuperadmin();

        group.MapGet("/list", async (HttpContext ctx, HardDeleteService svc, SessionService session, int page = 1, int pageSize = 100, string? filter = null, HardDeleteStatusFilter status = HardDeleteStatusFilter.All) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var result = await svc.ListAsync(page, pageSize, filter, status, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/folder/preview", async (PreviewFolderRequest req, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var result = await svc.PreviewFolderAsync(req.Path, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/article/{id:guid}", async (Guid id, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, _) = CallerIdentity.Extract(ctx);

            var result = await svc.DeleteArticleAsync(id, userId, agentId, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/folder", async (HardDeleteFolderRequest req, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, _) = CallerIdentity.Extract(ctx);

            var result = await svc.DeleteFolderAsync(req.Path, userId, agentId, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/restore/article/{id:guid}", async (Guid id, RestoreService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var result = await svc.RestoreArticleAsync(id);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 404); }
            catch (InvalidOperationException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 400); }
        });

        group.MapPost("/restore/folder/{id:guid}", async (Guid id, RestoreService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var result = await svc.RestoreFolderAsync(id);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 404); }
            catch (InvalidOperationException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 400); }
        });

        group.MapGet("/audit", async (HardDeleteService svc, SessionService session, int page = 1, int pageSize = 100) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var result = await svc.ListAuditAsync(page, pageSize);
            return Results.Ok(result);
        });
    }
}

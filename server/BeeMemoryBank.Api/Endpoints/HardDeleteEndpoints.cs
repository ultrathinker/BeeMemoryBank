using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Core.Exceptions;

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
        }).SingleFlight("hard-delete-article:{id}", "Deleting this article for good");

        group.MapPost("/folder", async (HardDeleteFolderRequest req, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, _) = CallerIdentity.Extract(ctx);

            var result = await svc.DeleteFolderAsync(req.Path, userId, agentId, ctx.RequestAborted);
            return Results.Ok(result);
        }).SingleFlight("hard-delete-folder:{path}", "Deleting this folder for good", ctx =>
            ctx.Arguments.OfType<HardDeleteFolderRequest>().FirstOrDefault() is { Path: { Length: > 0 } path } ? "hard-delete-folder:" + path.Trim('/').ToLowerInvariant() : null);

        group.MapPost("/restore/article/{id:guid}", async (Guid id, RestoreService svc, SessionService session, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                using var stop = BeeMemoryBank.Api.Helpers.RequestCancellation.Linked(ctx);
                var result = await svc.RestoreArticleAsync(id, stop.Token);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 404); }
            catch (InvalidOperationException ex) when (ex is not MetadataKeyPublishPendingException) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 400); }
        }).SingleFlight("restore-article:{id}", "Restoring this article");

        group.MapPost("/restore/folder/{id:guid}", async (Guid id, RestoreService svc, SessionService session, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                using var stop = BeeMemoryBank.Api.Helpers.RequestCancellation.Linked(ctx);
                var result = await svc.RestoreFolderAsync(id, stop.Token);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 404); }
            catch (InvalidOperationException ex) when (ex is not MetadataKeyPublishPendingException) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 400); }
        }).SingleFlight("restore-folder:{id}", "Restoring this folder");

        group.MapGet("/audit", async (HardDeleteService svc, SessionService session, int page = 1, int pageSize = 100) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var result = await svc.ListAuditAsync(page, pageSize);
            return Results.Ok(result);
        });
    }
}

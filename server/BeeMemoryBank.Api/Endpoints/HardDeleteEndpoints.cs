using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

public static class HardDeleteEndpoints
{
    public static void MapHardDeleteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/hard-delete").WithTags("HardDelete");

        group.MapGet("/list", async (HttpContext ctx, HardDeleteService svc, SessionService session, int page = 1, int pageSize = 100, string? filter = null, HardDeleteStatusFilter status = HardDeleteStatusFilter.All) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var result = await svc.ListAsync(page, pageSize, filter, status, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/folder/preview", async (PreviewFolderRequest req, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var result = await svc.PreviewFolderAsync(req.Path, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/article/{id:guid}", async (Guid id, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var result = await svc.DeleteArticleAsync(id, userId, agentId, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/folder", async (HardDeleteFolderRequest req, HttpContext ctx, HardDeleteService svc, SessionService session) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var result = await svc.DeleteFolderAsync(req.Path, userId, agentId, ctx.RequestAborted);
            return Results.Ok(result);
        });

        group.MapPost("/restore/article/{id:guid}", async (Guid id, HttpContext ctx, RestoreService svc, SessionService session) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            try
            {
                var result = await svc.RestoreArticleAsync(id);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 404); }
            catch (InvalidOperationException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 400); }
        });

        group.MapPost("/restore/folder/{id:guid}", async (Guid id, HttpContext ctx, RestoreService svc, SessionService session) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            try
            {
                var result = await svc.RestoreFolderAsync(id);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 404); }
            catch (InvalidOperationException ex) { return Results.Json(new ErrorResponse(ex.Message), statusCode: 400); }
        });

        group.MapGet("/audit", async (HttpContext ctx, HardDeleteService svc, SessionService session, int page = 1, int pageSize = 100) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var result = await svc.ListAuditAsync(page, pageSize);
            return Results.Ok(result);
        });
    }
}

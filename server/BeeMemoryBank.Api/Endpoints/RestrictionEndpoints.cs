using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class RestrictionEndpoints
{
    public static void MapRestrictionEndpoints(this WebApplication app)
    {
        // ─── User restrictions ───────────────────────────────────────────────

        var userGroup = app.MapGroup("/api/restrictions/user").WithTags("Restrictions");

        userGroup.MapGet("/{userId:int}", async (int userId, IFolderRestrictionRepository repo, IFolderRepository folderRepo, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            var restrictions = await repo.GetByUserIdAsync(userId);
            var result = new List<object>();
            foreach (var r in restrictions)
            {
                var folder = await folderRepo.GetByIdAsync(r.FolderId);
                result.Add(new
                {
                    id = r.Id,
                    folderId = r.FolderId,
                    folderPath = folder?.Path ?? "(deleted)",
                    createdAt = r.CreatedAt
                });
            }
            return Results.Ok(result);
        });

        userGroup.MapPost("/{userId:int}", async (int userId, AddRestrictionRequest req, IFolderRestrictionRepository repo, IFolderRepository folderRepo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            var folder = await folderRepo.GetByIdAsync(req.FolderId);
            if (folder == null)
                return Results.Json(new ErrorResponse("Folder not found"), statusCode: 404);

            var restriction = new FolderRestriction
            {
                UserId = userId,
                FolderId = req.FolderId,
                CreatedAt = DateTime.UtcNow
            };
            try
            {
                await repo.AddAsync(restriction);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Json(new ErrorResponse("Restriction already exists"), statusCode: 409);
            }
            folderAccess.InvalidateCache(userId, null);
            return Results.Ok(new { restriction.Id, folderId = restriction.FolderId, folderPath = folder.Path });
        });

        userGroup.MapDelete("/{userId:int}/{folderId:guid}", async (int userId, Guid folderId, IFolderRestrictionRepository repo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            await repo.RemoveByUserAndFolderAsync(userId, folderId);
            folderAccess.InvalidateCache(userId, null);
            return Results.NoContent();
        });

        // ─── Agent restrictions ──────────────────────────────────────────────

        var agentGroup = app.MapGroup("/api/restrictions/agent").WithTags("Restrictions");

        agentGroup.MapGet("/{agentId:int}", async (int agentId, IFolderRestrictionRepository repo, IFolderRepository folderRepo, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            var restrictions = await repo.GetByAgentIdAsync(agentId);
            var result = new List<object>();
            foreach (var r in restrictions)
            {
                var folder = await folderRepo.GetByIdAsync(r.FolderId);
                result.Add(new
                {
                    id = r.Id,
                    folderId = r.FolderId,
                    folderPath = folder?.Path ?? "(deleted)",
                    createdAt = r.CreatedAt
                });
            }
            return Results.Ok(result);
        });

        agentGroup.MapPost("/{agentId:int}", async (int agentId, AddRestrictionRequest req, IFolderRestrictionRepository repo, IFolderRepository folderRepo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            var folder = await folderRepo.GetByIdAsync(req.FolderId);
            if (folder == null)
                return Results.Json(new ErrorResponse("Folder not found"), statusCode: 404);

            var restriction = new FolderRestriction
            {
                AgentId = agentId,
                FolderId = req.FolderId,
                CreatedAt = DateTime.UtcNow
            };
            try
            {
                await repo.AddAsync(restriction);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Json(new ErrorResponse("Restriction already exists"), statusCode: 409);
            }
            folderAccess.InvalidateCache(null, agentId);
            return Results.Ok(new { restriction.Id, folderId = restriction.FolderId, folderPath = folder.Path });
        });

        agentGroup.MapDelete("/{agentId:int}/{folderId:guid}", async (int agentId, Guid folderId, IFolderRestrictionRepository repo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            await repo.RemoveByAgentAndFolderAsync(agentId, folderId);
            folderAccess.InvalidateCache(null, agentId);
            return Results.NoContent();
        });
    }
}

internal record AddRestrictionRequest(Guid FolderId);

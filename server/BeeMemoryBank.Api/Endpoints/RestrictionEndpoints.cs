// Folder ACL entry endpoints. ACL entries are node-local: they
// live only on this node and are not propagated via sync. See
// docs/architecture.md → Node Topology.

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
        var userGroup = app.MapGroup("/api/restrictions/user").WithTags("Restrictions");

        userGroup.MapGet("/{userId:int}", async (int userId, IFolderAclRepository repo, IFolderRepository folderRepo, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            var entries = await repo.GetByUserIdAsync(userId);
            var result = new List<object>();
            foreach (var e in entries)
            {
                var folder = await folderRepo.GetByIdAsync(e.FolderId);
                result.Add(new
                {
                    id = e.Id,
                    folderId = e.FolderId,
                    folderPath = folder?.Path ?? "(deleted)",
                    effect = e.Effect.ToString().ToLowerInvariant(),
                    createdAt = e.CreatedAt
                });
            }
            return Results.Ok(result);
        });

        userGroup.MapPost("/{userId:int}", async (int userId, AddAclEntryRequest req, IFolderAclRepository repo, IFolderRepository folderRepo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            if (!Enum.TryParse<AclEffect>(req.Effect, ignoreCase: true, out var effect))
                return Results.BadRequest(new ErrorResponse("Invalid effect. Use 'allow' or 'deny'."));

            var folder = await folderRepo.GetByIdAsync(req.FolderId);
            if (folder == null)
                return Results.Json(new ErrorResponse("Folder not found"), statusCode: 404);

            var entry = new FolderAclEntry
            {
                UserId = userId,
                FolderId = req.FolderId,
                Effect = effect,
                CreatedAt = DateTime.UtcNow
            };
            try
            {
                await repo.AddAsync(entry);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Json(new ErrorResponse("ACL entry already exists"), statusCode: 409);
            }
            folderAccess.InvalidateCache(userId);
            return Results.Ok(new { entry.Id, folderId = entry.FolderId, folderPath = folder.Path, effect = entry.Effect.ToString().ToLowerInvariant() });
        });

        userGroup.MapDelete("/{userId:int}/{folderId:guid}", async (int userId, Guid folderId, IFolderAclRepository repo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            await repo.RemoveByUserAndFolderAsync(userId, folderId);
            folderAccess.InvalidateCache(userId);
            return Results.NoContent();
        });
    }
}

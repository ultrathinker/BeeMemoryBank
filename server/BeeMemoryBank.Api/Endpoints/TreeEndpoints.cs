using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class TreeEndpoints
{
    public static void MapTreeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tree", async (HttpContext ctx, TreeService svc, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var tree = await svc.GetTreeAsync();

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                if (restrictedPaths.Count > 0)
                {
                    var keysToRemove = tree.Keys
                        .Where(k => FolderAccessService.IsPathRestricted(restrictedPaths, k))
                        .ToList();
                    foreach (var key in keysToRemove)
                        tree.Remove(key);

                    foreach (var entry in tree)
                    {
                        entry.Value.RemoveAll(child => FolderAccessService.IsPathRestricted(restrictedPaths, child));
                    }
                }
            }

            return Results.Ok(tree);
        }).WithTags("Tree");

        app.MapGet("/api/tree/children", async (HttpContext ctx, TreeService svc, FolderAccessService folderAccess, string path = "/") =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                if (FolderAccessService.IsPathRestricted(restrictedPaths, path))
                    return Results.NotFound(new ErrorResponse($"Path '{path}' not found"));
            }

            var result = await svc.GetChildrenAsync(path);

            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                result.Folders = result.Folders.Where(f => !FolderAccessService.IsPathRestricted(restrictedPaths, f.Path)).ToList();
                result.Articles = FolderAccessService.FilterArticles(result.Articles, restrictedPaths);
            }

            var response = new TreeChildrenResponse(
                result.Path,
                result.Folders.Select(f => new FolderInfoResponse(f.Id, f.Path, f.Name, f.ArticleCount)).ToList(),
                result.Articles.Select(ArticleResponse.From).ToList());
            return Results.Ok(response);
        }).WithTags("Tree");

        app.MapGet("/api/folders/search", async (HttpContext ctx, IFolderRepository folderRepo, string? q = null, int limit = 12) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(q))
                return Results.Ok(Array.Empty<object>());

            limit = Math.Clamp(limit, 1, 50);
            var folders = await folderRepo.SearchAsync(q);
            var hasMore = folders.Count > limit;
            var results = folders.Take(limit).Select(f => new { id = f.Id, path = f.Path }).ToList();
            return Results.Ok(new { folders = results, hasMore });
        }).WithTags("Tree");
    }
}

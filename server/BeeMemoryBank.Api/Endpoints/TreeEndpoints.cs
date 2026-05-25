using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class TreeEndpoints
{
    public static void MapTreeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tree", async (HttpContext ctx, TreeService svc, CallerScopeHolder scopeHolder) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            // Folders come from FolderRepository already filtered by ambient scope
            // to the navigable set (readable paths + ancestor stubs for AllowList).
            // TreeService builds the parent→children map from that set, so for an
            // AllowList user with "/Work/Project2" the tree correctly contains
            //   "/"                -> ["/Work"]      (ancestor stub)
            //   "/Work"            -> ["/Work/Project2"]   (ancestor stub, siblings hidden)
            //   "/Work/Project2"   -> [...]          (fully readable)
            var tree = await svc.GetTreeAsync();

            // Defence-in-depth: drop any key/child that is not navigable (should already be filtered).
            var scope = scopeHolder.Scope;
            if (!scope.IsSuperadmin)
            {
                var keysToRemove = tree.Keys.Where(k => !scope.IsNavigable(k)).ToList();
                foreach (var key in keysToRemove)
                    tree.Remove(key);

                foreach (var entry in tree)
                    entry.Value.RemoveAll(child => !scope.IsNavigable(child));
            }

            return Results.Ok(tree);
        }).WithTags("Tree");

        app.MapGet("/api/tree/children", async (HttpContext ctx, TreeService svc, CallerScopeHolder scopeHolder, IConceptTagRepository conceptTagRepo, FolderAccessService folderAccess, IFolderRepository folderRepo, string path = "/") =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var scope = scopeHolder.Scope;
            if (!scope.IsNavigable(path))
                return Results.NotFound(new ErrorResponse($"Path '{path}' not found"));

            var result = await svc.GetChildrenAsync(path);

            var articleResponses = new List<ArticleResponse>();
            var articleIds = result.Articles.Select(a => a.Id).ToList();
            var tagMap = await conceptTagRepo.GetByArticleIdsAsync(articleIds);
            foreach (var a in result.Articles)
            {
                var conceptTags = tagMap.GetValueOrDefault(a.Id, new List<string>());
                articleResponses.Add(ArticleResponse.From(a, conceptTags));
            }

            var isReadOnly = false;
            if (!scope.IsSuperadmin)
            {
                var (userId, _, _) = CallerIdentity.Extract(ctx);
                if (userId is int uid)
                {
                    var (_, _, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(uid);
                    isReadOnly = FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, path);
                }
            }

            var currentFolder = await folderRepo.GetByPathAsync(path);
            var isCurrentSystem = currentFolder?.IsSystem == true;

            var response = new TreeChildrenResponse(
                result.Path,
                result.Folders.Select(f => new FolderInfoResponse(f.Id, f.Path, f.Name, f.ArticleCount, f.CreatedAt, f.UpdatedAt, f.IsSystem, f.IsRemote)).ToList(),
                articleResponses,
                isReadOnly,
                isCurrentSystem);
            return Results.Ok(response);
        }).WithTags("Tree");

        // Lightweight endpoint UIs use to decorate folder lists and to gate
        // single-folder write actions without re-listing the whole tree.
        app.MapGet("/api/access/readonly-paths", async (HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (isSuperadmin || userId is null)
                return Results.Ok(new { paths = Array.Empty<string>() });

            var (_, _, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
            return Results.Ok(new { paths = readOnlyPaths.OrderBy(p => p).ToArray() });
        }).WithTags("Access");

        app.MapGet("/api/access/folder-permissions", async (HttpContext ctx, FolderAccessService folderAccess, string path) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("path is required"));

            var (userId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (isSuperadmin)
                return Results.Ok(new { path, canRead = true, canWrite = true, isReadOnly = false });
            if (userId is null)
                return Results.Ok(new { path, canRead = false, canWrite = false, isReadOnly = false });

            var (deny, allow, ro) = await folderAccess.GetFullAccessInfoAsync(userId);
            var canRead = !FolderAccessService.IsAccessDenied(deny, allow, path);
            var isReadOnly = canRead && FolderAccessService.IsReadOnlyForCaller(ro, path);
            return Results.Ok(new { path, canRead, canWrite = canRead && !isReadOnly, isReadOnly });
        }).WithTags("Access");

        app.MapGet("/api/folders/search", async (HttpContext ctx, IFolderRepository folderRepo, string? q = null, int limit = 12) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(q))
                return Results.Ok(Array.Empty<object>());

            limit = Math.Clamp(limit, 1, 100);
            var folders = await folderRepo.SearchAsync(q);

            folders = folders
                .OrderBy(f => f.Path.Length)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hasMore = folders.Count > limit;
            var results = folders.Take(limit).Select(f => new { id = f.Id, path = f.Path }).ToList();
            return Results.Ok(new { folders = results, hasMore });
        }).WithTags("Tree");
    }
}

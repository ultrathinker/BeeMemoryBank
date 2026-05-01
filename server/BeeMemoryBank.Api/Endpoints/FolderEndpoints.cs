using System.IO.Compression;
using System.Text;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/folders").WithTags("Folders");

        group.MapPost("/", async (CreateFolderRequest req, FolderService folderSvc, SessionService session, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new ErrorResponse("Path is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, req.Path))
                    return Results.Json(new ErrorResponse($"You don't have permission to create a folder at {PathHelper.Display(req.Path)}."), statusCode: 403);
            }

            try
            {
                var folder = await folderSvc.CreateAsync(req.Path);
                return Results.Ok(new FolderCreateResult(folder.Id, folder.Path, folder.Name));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse($"You don't have permission to create a folder at {PathHelper.Display(req.Path)}."), statusCode: 403);
            }
        });

        group.MapGet("/download", async (ArticleService svc, SessionService session, HttpContext ctx, string path) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var articles = await svc.ListAsync(path);

            if (articles.Count == 0)
                return Results.BadRequest(new ErrorResponse("Folder is empty"));

            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var article in articles)
                {
                    var content = await svc.GetContentAsync(article.Id);

                    var relative = article.TreePath[path.TrimEnd('/').Length..].TrimStart('/');
                    var folder = relative.Length > 0 ? relative + "/" : "";
                    var fileName = Helpers.FileNameHelper.SanitizeFileName(article.Title) + ".md";
                    var entryPath = folder + fileName;

                    var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                    await writer.WriteAsync(content);
                }
            }

            ms.Position = 0;
            var folderName = path.TrimEnd('/').Split('/').LastOrDefault("folder");
            var zipName = Helpers.FileNameHelper.SanitizeFileName(folderName) + ".zip";
            return Results.File(ms, "application/zip", zipName);
        });

        group.MapPatch("/", async (string path, RenameFolderRequest req, IFolderRepository folderRepo, FolderService folderSvc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            HashSet<string> denyPaths = [];
            HashSet<string> allowPaths = [];
            if (!isSuperadmin)
            {
                (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, path))
                    return Results.Json(new ErrorResponse($"You don't have permission to access folder {PathHelper.Display(path)}."), statusCode: 403);
            }

            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return Results.NotFound(new ErrorResponse($"Folder '{path}' not found"));

            var newName = req.NewPath.Split('/').Last();

            if (!isSuperadmin)
            {
                var parentPath = folder.ParentPath ?? "";
                var resolvedNewPath = (parentPath.Length > 0 ? parentPath.TrimEnd('/') + "/" : "/") + newName;
                resolvedNewPath = "/" + resolvedNewPath.Trim('/');
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, resolvedNewPath))
                    return Results.Json(new ErrorResponse("You don't have permission to rename the folder to this path."), statusCode: 403);
            }

            try
            {
                await folderSvc.RenameAsync(folder.Id, newName);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse("Permission denied for this rename operation."), statusCode: 403);
            }

            var updated = await folderRepo.GetByIdAsync(folder.Id);
            var actualNewPath = updated?.Path ?? req.NewPath;
            return Results.Ok(new FolderRenameResult(path, actualNewPath, 0));
        });

        group.MapPost("/move", async (string path, MoveFolderRequest req, IFolderRepository folderRepo, FolderService folderSvc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, path))
                    return Results.Json(new ErrorResponse("You don't have permission to access this folder."), statusCode: 403);

                var folderName = path.TrimEnd('/').Split('/').Last();
                var resolvedNewPath = req.NewParentPath.TrimEnd('/') + "/" + folderName;
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, resolvedNewPath))
                    return Results.Json(new ErrorResponse("You don't have permission to access this folder."), statusCode: 403);
            }

            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return Results.NotFound(new ErrorResponse($"Folder '{path}' not found"));

            try
            {
                var folderName = path.TrimEnd('/').Split('/').Last();
                var newPath = req.NewParentPath.TrimEnd('/') + "/" + folderName;
                await folderSvc.MoveAsync(folder.Id, req.NewParentPath);
                return Results.Ok(new FolderMoveResult(path, newPath, 0));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        });

        group.MapDelete("/", async (string path, ArticleService svc, IFolderRepository folderRepo, FolderService folderSvc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, path))
                    return Results.Json(new ErrorResponse($"You don't have permission to delete folder {PathHelper.Display(path)}."), statusCode: 403);

                if (allowPaths.Count == 0)
                {
                    var pathPrefix = path.TrimEnd('/') + "/";
                    if (denyPaths.Any(rp => rp.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
                        return Results.Json(new ErrorResponse("Cannot delete: folder contains restricted sub-folders"), statusCode: 403);
                }
            }

            var deleted = await svc.DeleteByPathAsync(path);

            var folder = await folderRepo.GetByPathAsync(path);
            if (folder != null)
                await folderSvc.DeleteAsync(folder.Id);

            return Results.Ok(new FolderDeleteResult(path, deleted));
        });
    }
}

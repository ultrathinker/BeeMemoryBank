using System.IO.Compression;
using System.Text;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/folders").WithTags("Folders").RequireInternalKey();

        group.MapPost("/", async (CreateFolderRequest req, FolderService folderSvc, SessionService session, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new ErrorResponse("Path is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, req.Path))
                    return Results.Json(new ErrorResponse($"You don't have permission to create a folder at {PathHelper.Display(req.Path)}."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, req.Path))
                    return Results.Json(new ErrorResponse($"Folder {PathHelper.Display(req.Path)} is read-only for your user."), statusCode: 403);
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
            catch (UnauthorizedAccessException ex)
            {
                WriteAclDenial.TryClassify(ex, out var kind, out var path);
                var message = kind == WriteAclDenialKind.ReadOnly
                    ? $"Folder {PathHelper.Display(path)} is read-only for your user."
                    : $"You don't have permission to create a folder at {PathHelper.Display(req.Path)}.";
                return Results.Json(new ErrorResponse(message), statusCode: 403);
            }
        });

        group.MapGet("/download", async (ArticleService svc, SessionService session, HttpContext ctx, string path) =>
        {
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
                    // Don't write a password-protected article's raw BMBENC1 blob into the export.
                    if (BeeMemoryBank.Crypto.ProtectedContentCodec.IsProtected(content))
                        content = "🔒 This article is password-protected (second-layer encryption) and was not included in this export.\n";

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
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            HashSet<string> denyPaths = [];
            HashSet<string> allowPaths = [];
            HashSet<string> readOnlyPaths = [];
            if (!isSuperadmin)
            {
                (denyPaths, allowPaths, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, path))
                    return Results.Json(new ErrorResponse($"You don't have permission to access folder {PathHelper.Display(path)}."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, path))
                    return Results.Json(new ErrorResponse($"Folder {PathHelper.Display(path)} is read-only for your user."), statusCode: 403);
            }

            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return Results.NotFound(new ErrorResponse($"Folder '{path}' not found"));

            // L6: TrimEnd('/') before splitting -- "/Foo/" used to split into ["", "Foo", ""],
            // so .Last() picked up the trailing EMPTY segment as the new name. RenameAsync then
            // threw ArgumentException("Folder name cannot be empty.") for a request that looks
            // completely reasonable to the caller, and (see the catch clauses below) nothing
            // caught ArgumentException here, so it surfaced as an unhandled 500.
            var newName = req.NewPath.TrimEnd('/').Split('/').Last();

            if (!isSuperadmin)
            {
                var parentPath = folder.ParentPath ?? "";
                var resolvedNewPath = (parentPath.Length > 0 ? parentPath.TrimEnd('/') + "/" : "/") + newName;
                resolvedNewPath = "/" + resolvedNewPath.Trim('/');
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, resolvedNewPath))
                    return Results.Json(new ErrorResponse("You don't have permission to rename the folder to this path."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, resolvedNewPath))
                    return Results.Json(new ErrorResponse("Target path is read-only for your user."), statusCode: 403);
            }

            try
            {
                await folderSvc.RenameAsync(folder.Id, newName);
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteAclDenial.TryClassify(ex, out var kind, out var deniedPath);
                var message = kind == WriteAclDenialKind.ReadOnly
                    ? $"Folder {PathHelper.Display(deniedPath)} is read-only for your user."
                    : "Permission denied for this rename operation.";
                return Results.Json(new ErrorResponse(message), statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 403);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            var updated = await folderRepo.GetByIdAsync(folder.Id);
            var actualNewPath = updated?.Path ?? req.NewPath;
            return Results.Ok(new FolderRenameResult(path, actualNewPath, 0));
        });

        group.MapPost("/move", async (string path, MoveFolderRequest req, IFolderRepository folderRepo, FolderService folderSvc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, path))
                    return Results.Json(new ErrorResponse("You don't have permission to access this folder."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, path))
                    return Results.Json(new ErrorResponse("Source folder is read-only for your user."), statusCode: 403);

                var folderName = path.TrimEnd('/').Split('/').Last();
                var resolvedNewPath = req.NewParentPath.TrimEnd('/') + "/" + folderName;
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, resolvedNewPath))
                    return Results.Json(new ErrorResponse("You don't have permission to access this folder."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, resolvedNewPath))
                    return Results.Json(new ErrorResponse("Target parent folder is read-only for your user."), statusCode: 403);
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
            // L6: the pre-checks above re-validate against a cache snapshot (GetFullAccessInfoAsync,
            // 60s TTL) that can be stale relative to what folderSvc.MoveAsync itself enforces at
            // write time -- and MoveAsync's own descendant-rewrite path can throw for reasons the
            // pre-checks never model at all. Without this, that throw propagated as an unhandled
            // 500 instead of the 403 every other ACL denial in this file returns.
            catch (UnauthorizedAccessException ex)
            {
                WriteAclDenial.TryClassify(ex, out var kind, out var deniedPath);
                var message = kind == WriteAclDenialKind.ReadOnly
                    ? $"Folder {PathHelper.Display(deniedPath)} is read-only for your user."
                    : "You don't have permission to complete this move.";
                return Results.Json(new ErrorResponse(message), statusCode: 403);
            }
        });

        group.MapDelete("/", async (string path, ArticleService svc, IFolderRepository folderRepo, FolderService folderSvc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new ErrorResponse("Parameter 'path' is required"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, path))
                    return Results.Json(new ErrorResponse($"You don't have permission to delete folder {PathHelper.Display(path)}."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, path))
                    return Results.Json(new ErrorResponse($"Folder {PathHelper.Display(path)} is read-only for your user."), statusCode: 403);

                if (allowPaths.Count == 0)
                {
                    var pathPrefix = path.TrimEnd('/') + "/";
                    if (denyPaths.Any(rp => rp.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
                        return Results.Json(new ErrorResponse("Cannot delete: folder contains restricted sub-folders"), statusCode: 403);
                }
            }

            // PRE-CHECK before touching articles. Without this, a folder DELETE
            // first calls ArticleService.DeleteByPathAsync (wiping articles)
            // and THEN tries FolderService.DeleteAsync which throws when the
            // folder is system/remote — leaving us with deleted articles but a
            // surviving folder, and the next remote-sync poll resurrects the
            // articles under new GUIDs. Caught by Phase 3 E2E test.
            var existing = await folderRepo.GetByPathAsync(path);
            if (existing != null && existing.IsSystem)
                return Results.Json(new ErrorResponse($"System folder {PathHelper.Display(path)} cannot be deleted."), statusCode: 403);
            if (existing != null && existing.RemoteSubscriptionId.HasValue)
                return Results.Json(new ErrorResponse(
                    $"Folder {PathHelper.Display(path)} is a remote mirror. Detach the subscription on the Remote Accounts page instead."),
                    statusCode: 403);

            // Same protection for ancestors: refuse if any descendant is itself a remote mount.
            if (existing != null)
            {
                var pathPrefix = path.TrimEnd('/') + "/";
                var allFolders = await folderRepo.GetAllActiveAsync();
                var mirrorBlocker = allFolders.FirstOrDefault(f =>
                    f.RemoteSubscriptionId.HasValue
                    && f.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));
                if (mirrorBlocker != null)
                    return Results.Json(new ErrorResponse(
                        $"Folder {PathHelper.Display(path)} contains a remote mirror at {PathHelper.Display(mirrorBlocker.Path)}. Detach the subscription first."),
                        statusCode: 403);
            }

            // L6: folderSvc.DeleteAsync (via SoftDeleteByPathPrefixAsync's H1 descendant walk) and
            // EnsureNoRemoteDescendantsAsync can both still throw here even after the pre-checks
            // above -- the pre-checks re-validate against a 60s-TTL cache snapshot and, for the
            // descendant-deny case, only ever covered the allowPaths.Count == 0 shape. Without a
            // catch here those exceptions propagated as unhandled 500s instead of the 403/409 every
            // other ACL/business-rule denial in this file returns.
            try
            {
                var folder = await folderRepo.GetByPathAsync(path);

                // Validate BEFORE destroying anything. DeleteByPathAsync below removes this
                // folder's articles, and folderSvc.DeleteAsync's own guards (system, remote mirror,
                // remote descendants, and the H1 descendant write-ACL walk) would otherwise not run
                // until after that — turning a correctly-denied 403 into a 403 that already deleted
                // the caller's articles. Same trap the system/remote pre-check above was added for;
                // EnsureDeletableAsync is the authoritative, non-mutating form of every guard
                // DeleteAsync enforces.
                if (folder != null)
                    await folderSvc.EnsureDeletableAsync(folder.Id);

                var deleted = await svc.DeleteByPathAsync(path);

                if (folder != null)
                    await folderSvc.DeleteAsync(folder.Id);

                return Results.Ok(new FolderDeleteResult(path, deleted));
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteAclDenial.TryClassify(ex, out var kind, out var deniedPath);
                var message = kind == WriteAclDenialKind.ReadOnly
                    ? $"Folder {PathHelper.Display(deniedPath)} is read-only for your user."
                    : "Cannot delete: folder contains restricted sub-folders.";
                return Results.Json(new ErrorResponse(message), statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 403);
            }
        });
    }
}

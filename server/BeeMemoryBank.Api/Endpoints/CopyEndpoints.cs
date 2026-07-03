using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class CopyEndpoints
{
    public static void MapCopyEndpoints(this WebApplication app)
    {
        app.MapPost("/api/articles/{id:guid}/copy", async (
            Guid id,
            CopyArticleRequest req,
            CopyService copy,
            ArticleService articleSvc,
            FolderAccessService folderAccess,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.TargetFolderPath))
                return Results.BadRequest(new ErrorResponse("targetFolderPath is required"));

            // SOURCE ACL gate (gemini+kilo security review):
            // Without this, any caller who guessed a GUID could "copy" a foreign
            // article into their own writable folder and effectively exfiltrate
            // its plaintext. The repo-level write guard fires on the target only.
            var (cuId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var source = await articleSvc.GetMetadataAsync(id);
                if (source == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));
                var (deny, allow, _) = await folderAccess.GetFullAccessInfoAsync(cuId);
                if (FolderAccessService.IsAccessDenied(deny, allow, source.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to read the source article."), statusCode: 403);
            }

            try
            {
                var newId = await copy.CopyArticleAsync(id, req.TargetFolderPath);
                return Results.Ok(new { newArticleId = newId, targetFolderPath = req.TargetFolderPath });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ErrorResponse(ex.Message));
            }
            catch (ReadOnlyAccessException ex)
            {
                return Results.Json(new ErrorResponse($"Target folder '{ex.Path}' is read-only for your user."), statusCode: 403);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse("Permission denied for this copy."), statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).RequireInternalKey().WithTags("Copy");

        app.MapPost("/api/folders/{id:guid}/copy", async (
            Guid id,
            CopyFolderRequest req,
            CopyService copy,
            BeeMemoryBank.Core.Interfaces.IFolderRepository folderRepo,
            FolderAccessService folderAccess,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.TargetParentPath))
                return Results.BadRequest(new ErrorResponse("targetParentPath is required"));

            // SOURCE ACL gate — same reason as in the article copy handler.
            var (cuId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var source = await folderRepo.GetByIdAsync(id);
                if (source == null)
                    return Results.NotFound(new ErrorResponse($"Folder {id} not found"));
                var (deny, allow, _) = await folderAccess.GetFullAccessInfoAsync(cuId);
                if (FolderAccessService.IsAccessDenied(deny, allow, source.Path))
                    return Results.Json(new ErrorResponse("You don't have permission to read the source folder."), statusCode: 403);
            }

            try
            {
                var newId = await copy.CopyFolderAsync(id, req.TargetParentPath);
                return Results.Ok(new { newFolderId = newId, targetParentPath = req.TargetParentPath });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ErrorResponse(ex.Message));
            }
            catch (ReadOnlyAccessException ex)
            {
                return Results.Json(new ErrorResponse($"Target folder '{ex.Path}' is read-only for your user."), statusCode: 403);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse("Permission denied for this copy."), statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).RequireInternalKey().WithTags("Copy");
    }
}

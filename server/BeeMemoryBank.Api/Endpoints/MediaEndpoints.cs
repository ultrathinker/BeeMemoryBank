using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/media").WithTags("Media").RequireInternalKey();

        group.MapPost("/", async (
            IFormFile file, SessionService session, MediaService mediaService,
            ArticleService articleSvc, FolderAccessService folderAccess,
            HttpContext ctx, [FromForm] string? articleId, [FromForm] bool attachment = false) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            Guid? artId = Guid.TryParse(articleId, out var parsed) ? parsed : null;

            if (artId.HasValue)
            {
                var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
                if (!isSuperadmin)
                {
                    var article = await articleSvc.GetMetadataAsync(artId.Value);
                    if (article != null)
                    {
                        var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                        if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                            return Results.Json(new ErrorResponse($"Access denied for path '{article.TreePath}'."), statusCode: 403);
                        // RO ACL was missing here — user with allow+is_read_only could upload
                        // media binding it to articles in a "read-only" folder. Caught by E2E.
                        if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, article.TreePath))
                            return Results.Json(new ErrorResponse($"Folder '{article.TreePath}' is read-only for your user."), statusCode: 403);
                    }
                }
            }

            // Cap upload size before buffering. Without this, a multi-GB upload would buffer
            // into MemoryStream (LOH fragmentation, OOM risk). 100MB matches typical media
            // uploads (images, audio); raise via env var if a larger workflow comes up.
            const long MaxUploadBytes = 100L * 1024 * 1024;
            if (file.Length > MaxUploadBytes)
                return Results.Json(
                    new ErrorResponse($"File too large ({file.Length} bytes); limit is {MaxUploadBytes} bytes."),
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var plaintext = ms.ToArray();

            try
            {
                // An unlinked upload (no articleId) remembers its uploader: until an article owns it
                // there is no folder ACL to go by, and only that uploader may link, read or delete it.
                var media = await mediaService.CreateAsync(file.FileName, file.ContentType, plaintext, artId,
                    isAttachment: attachment, uploadedBy: CallerIdentity.Extract(ctx).MediaOwnerKey);
                return Results.Created($"/api/media/{media.Id}", new
                {
                    id = media.Id,
                    fileName = media.FileName,
                    contentType = media.ContentType,
                    fileSize = media.FileSize,
                    kind = media.Kind
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        }).DisableAntiforgery();

        group.MapGet("/{id:guid}", async (
            Guid id, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var result = await mediaService.GetContentAsync(id);
            // The folder ACL hides every unlinked row from a non-superadmin — including the files
            // they just uploaded on the "new article" page. Let the uploader through for their own.
            var owner = CallerIdentity.Extract(ctx).MediaOwnerKey;
            if (result is null && owner != null)
                result = await mediaService.GetOwnedOrphanContentAsync(id, owner);
            if (result is null)
                return Results.NotFound();

            var (data, contentType, fileName) = result.Value;
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            BeeMemoryBank.Hosting.AspNetCore.UserContentResponseHeaders.ApplyTo(ctx.Response);
            return Results.File(data, contentType, fileName);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                await mediaService.DeleteAsync(id);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                // Same as GET: an unlinked row is invisible to a non-superadmin's ACL, so the
                // uploader removing a file from a not-yet-saved article would otherwise get 404.
                var owner = CallerIdentity.Extract(ctx).MediaOwnerKey;
                return owner != null && await mediaService.DeleteOwnedOrphanAsync(id, owner)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 403);
            }
        });

        app.MapGet("/api/articles/{articleId:guid}/media", async (
            Guid articleId, SessionService session, MediaService mediaService,
            ArticleService articleSvc, HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var article = await articleSvc.GetMetadataAsync(articleId);
            if (article == null)
                return Results.NotFound(new ErrorResponse("Article not found or access denied."));

            var media = await mediaService.GetByArticleIdAsync(articleId);
            return Results.Ok(media.Select(m => new
            {
                id = m.Id,
                fileName = m.FileName,
                contentType = m.ContentType,
                fileSize = m.FileSize,
                createdAt = m.CreatedAt,
                kind = m.Kind
            }));
        }).RequireInternalKey().WithTags("Media");
    }
}

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/media").WithTags("Media");

        group.MapPost("/", async (
            IFormFile file, SessionService session, MediaService mediaService,
            ArticleService articleSvc, FolderAccessService folderAccess,
            HttpContext ctx, string? articleId) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
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
                        var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                        if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                            return Results.Json(new ErrorResponse($"Access denied for path '{article.TreePath}'."), statusCode: 403);
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
                var media = await mediaService.CreateAsync(file.FileName, file.ContentType, plaintext, artId);
                return Results.Created($"/api/media/{media.Id}", new
                {
                    id = media.Id,
                    fileName = media.FileName,
                    contentType = media.ContentType,
                    fileSize = media.FileSize
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        group.MapGet("/{id:guid}", async (
            Guid id, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var result = await mediaService.GetContentAsync(id);
            if (result is null)
                return Results.NotFound();

            var (data, contentType, fileName) = result.Value;
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(data, contentType, fileName);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                await mediaService.DeleteAsync(id);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
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
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
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
                createdAt = m.CreatedAt
            }));
        }).WithTags("Media");
    }
}

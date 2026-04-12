using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/media").WithTags("Media");

        group.MapPost("/", async (IFormFile file, SessionService session, MediaService mediaService, HttpContext ctx, string? articleId) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var plaintext = ms.ToArray();

            Guid? artId = Guid.TryParse(articleId, out var parsed) ? parsed : null;

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

        group.MapGet("/{id:guid}", async (Guid id, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var (data, contentType, fileName) = await mediaService.GetContentAsync(id);
                ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                return Results.File(data, contentType, fileName);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            await mediaService.DeleteAsync(id);
            return Results.NoContent();
        });

        app.MapGet("/api/articles/{articleId:guid}/media", async (Guid articleId, SessionService session, MediaService mediaService, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

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

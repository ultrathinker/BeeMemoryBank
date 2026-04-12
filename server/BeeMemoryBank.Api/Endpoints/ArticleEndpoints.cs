using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/articles").WithTags("Articles");

        group.MapGet("/", async (HttpContext ctx, ArticleService svc, FolderAccessService folderAccess, string? treePath = null) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            var articles = await svc.ListAsync(treePath);

            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                articles = FolderAccessService.FilterArticles(articles, restrictedPaths);
            }

            return Results.Ok(articles.Select(ArticleResponse.From));
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, ArticleService svc, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                if (FolderAccessService.IsPathRestricted(restrictedPaths, article.TreePath))
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));
            }

            return Results.Ok(ArticleResponse.From(article));
        });

        group.MapGet("/{id:guid}/content", async (Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);

            if (!isSuperadmin)
            {
                var article = await svc.GetMetadataAsync(id);
                if (article == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));

                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                if (FolderAccessService.IsPathRestricted(restrictedPaths, article.TreePath))
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));
            }

            var content = await svc.GetContentAsync(id);
            return Results.Ok(new ArticleContentResponse(id, content));
        });

        group.MapPost("/", async (CreateArticleRequest req, ArticleService svc, SessionService session, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                if (FolderAccessService.IsPathRestricted(restrictedPaths, req.TreePath))
                    return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);
            }

            var article = await svc.CreateAsync(req.Title, req.TreePath, req.Tags ?? [], req.Content);
            return Results.Created($"/api/articles/{article.Id}", ArticleResponse.From(article));
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateArticleRequest req, ArticleService svc, SessionService session, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (req.Content != null && !session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));

                if (FolderAccessService.IsPathRestricted(restrictedPaths, existing.TreePath))
                    return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

                if (req.TreePath != null && FolderAccessService.IsPathRestricted(restrictedPaths, req.TreePath))
                    return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);
            }

            await svc.UpdateAsync(id, req.Title, req.TreePath, req.Tags, req.Content);
            var article = await svc.GetMetadataAsync(id);
            return article != null
                ? Results.Ok(ArticleResponse.From(article))
                : Results.NotFound(new ErrorResponse($"Article {id} not found"));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ArticleService svc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing != null && FolderAccessService.IsPathRestricted(restrictedPaths, existing.TreePath))
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));
            }

            await svc.DeleteAsync(id);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/move", async (Guid id, MoveArticleRequest req, ArticleService svc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));

                if (FolderAccessService.IsPathRestricted(restrictedPaths, existing.TreePath))
                    return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

                if (FolderAccessService.IsPathRestricted(restrictedPaths, req.NewPath))
                    return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);
            }

            await svc.MoveAsync(id, req.NewPath);
            return Results.Ok(new MoveArticleResponse(id, req.NewPath));
        });
    }
}

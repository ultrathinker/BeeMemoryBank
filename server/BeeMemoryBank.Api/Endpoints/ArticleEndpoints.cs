using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/articles").WithTags("Articles");

        group.MapGet("/", async (HttpContext ctx, ArticleService svc, IConceptTagRepository conceptTagRepo, string? treePath = null) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var articles = await svc.ListAsync(treePath);

            var results = new List<ArticleResponse>();
            var ids = articles.Select(a => a.Id).ToList();
            var tagMap = await conceptTagRepo.GetByArticleIdsAsync(ids);
            foreach (var a in articles)
            {
                var conceptTags = tagMap.GetValueOrDefault(a.Id, new List<string>());
                results.Add(ArticleResponse.From(a, conceptTags));
            }
            return Results.Ok(results);
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, ArticleService svc, IConceptTagRepository conceptTagRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);
            return Results.Ok(ArticleResponse.From(article, conceptTags));
        });

        group.MapGet("/{id:guid}/content", async (Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            // Folder ACL gate: GetContentAsync goes straight to the body repo (no scope filter),
            // so without this check any User-role caller can fetch plaintext for any article ID
            // they happen to know. GetMetadataAsync goes through ArticleRepository.GetByIdAsync
            // which returns null when the caller's scope denies the article's tree path.
            var meta = await svc.GetMetadataAsync(id);
            if (meta == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, meta.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to read this article."), statusCode: 403);
            }

            var content = await svc.GetContentAsync(id);
            return Results.Ok(new ArticleContentResponse(id, content));
        });

        group.MapPost("/", async (CreateArticleRequest req, ArticleService svc, ConceptTagService conceptTagSvc, SessionService session, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, req.TreePath))
                    return Results.Json(new ErrorResponse($"You don't have permission to create an article in {PathHelper.Display(req.TreePath)}."), statusCode: 403);
            }

            try
            {
                var article = await svc.CreateAsync(req.Title, req.TreePath, [], req.Content);
                if (req.ConceptTags is { Count: > 0 })
                    await conceptTagSvc.SetForArticleAsync(article.Id, req.ConceptTags);
                return Results.Created($"/api/articles/{article.Id}", ArticleResponse.From(article, req.ConceptTags ?? []));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse($"You don't have permission to create an article in {PathHelper.Display(req.TreePath)}."), statusCode: 403);
            }
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateArticleRequest req, ArticleService svc, ConceptTagService conceptTagSvc, IConceptTagRepository conceptTagRepo, SessionService session, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (req.Content != null && !session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));

                if (FolderAccessService.IsAccessDenied(folderPaths, policy, existing.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);

                if (req.TreePath != null && FolderAccessService.IsAccessDenied(folderPaths, policy, req.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);
            }
            else
            {
                // Superadmin path also needs an existence check — otherwise UpdateAsync throws
                // KeyNotFoundException → raw 500 for what should be a 404.
                if (await svc.GetMetadataAsync(id) == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));
            }

            await svc.UpdateAsync(id, req.Title, req.TreePath, null, req.Content);
            if (req.ConceptTags != null)
                await conceptTagSvc.SetForArticleAsync(id, req.ConceptTags);
            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));
            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);
            return Results.Ok(ArticleResponse.From(article, conceptTags));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ArticleService svc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing != null && FolderAccessService.IsAccessDenied(folderPaths, policy, existing.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to delete this article."), statusCode: 403);
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
                var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));

                if (FolderAccessService.IsAccessDenied(folderPaths, policy, existing.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);

                if (FolderAccessService.IsAccessDenied(folderPaths, policy, req.NewPath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);
            }

            await svc.MoveAsync(id, req.NewPath);
            return Results.Ok(new MoveArticleResponse(id, req.NewPath));
        });
    }
}

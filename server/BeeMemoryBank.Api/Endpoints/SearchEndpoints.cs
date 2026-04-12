using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", async (SearchService svc, SessionService session, HttpContext ctx, FolderAccessService folderAccess, string? q = null, bool content = false) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new ErrorResponse("Parameter 'q' is required"));

            if (content && !session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session must be unlocked for content search"), statusCode: 403);

            var results = content
                ? await svc.SearchWithContentAsync(q)
                : await svc.SearchAsync(q);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            var folders = results.Folders;
            var articles = results.Articles;
            if (!isSuperadmin)
            {
                var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                folders = FolderAccessService.FilterFolders(folders, restrictedPaths);
                articles = FolderAccessService.FilterArticles(articles, restrictedPaths);
            }

            return Results.Ok(new SearchResponse(
                folders.Select(f => FolderInfoResponse.From(f)).ToList(),
                articles.Select(ArticleResponse.From).ToList()
            ));
        }).WithTags("Search");

        app.MapPost("/api/search/semantic", async (
            SemanticSearchRequest req,
            EmbeddingProjectionService projectionService,
            IArticleRepository articleRepo,
            SessionService session,
            HttpContext ctx,
            FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new ErrorResponse("Query is required"));

            try
            {
                var queryProjection = await projectionService.ProjectQueryAsync(req.Query);
                var results = await articleRepo.SearchByEmbeddingAsync(queryProjection, Math.Clamp(req.TopK, 1, 100));

                var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
                if (!isSuperadmin)
                {
                    var restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);
                    results = FolderAccessService.FilterArticles(results, restrictedPaths);
                }

                return Results.Ok(results.Select(ArticleResponse.From));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        }).WithTags("Search");
    }
}

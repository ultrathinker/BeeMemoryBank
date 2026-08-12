using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", async (SearchService svc, SessionService session, HttpContext ctx, IConceptTagRepository conceptTagRepo, string? q = null, bool content = false) =>
        {

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new ErrorResponse("Parameter 'q' is required"));

            if (content && !session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session must be unlocked for content search"), statusCode: 403);

            var results = content
                ? await svc.SearchWithContentAsync(q)
                : await svc.SearchAsync(q);

            var folders = results.Folders;
            var articles = results.Articles;

            var articleResponses = new List<ArticleResponse>();
            var articleIds = articles.Select(a => a.Id).ToList();
            var tagMap = await conceptTagRepo.GetByArticleIdsAsync(articleIds);
            foreach (var a in articles)
            {
                var conceptTags = tagMap.GetValueOrDefault(a.Id, new List<string>());
                articleResponses.Add(ArticleResponse.From(a, conceptTags));
            }

            return Results.Ok(new SearchResponse(
                folders.Select(f => FolderInfoResponse.From(f)).ToList(),
                articleResponses
            ));
        }).RequireInternalKey().WithTags("Search");

        app.MapPost("/api/search/semantic", async (
            SemanticSearchRequest req,
            EmbeddingProjectionService projectionService,
            IArticleRepository articleRepo,
            IConceptTagRepository conceptTagRepo,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new ErrorResponse("Query is required"));

            try
            {
                var queryProjection = await projectionService.ProjectQueryAsync(req.Query);
                var results = await articleRepo.SearchByEmbeddingAsync(queryProjection, Math.Clamp(req.TopK, 1, 100));

                var articleResponses = new List<ArticleResponse>();
                var articleIds = results.Select(a => a.Id).ToList();
                var tagMap = await conceptTagRepo.GetByArticleIdsAsync(articleIds);
                foreach (var a in results)
                {
                    var conceptTags = tagMap.GetValueOrDefault(a.Id, new List<string>());
                    articleResponses.Add(ArticleResponse.From(a, conceptTags));
                }
                return Results.Ok(articleResponses);
            }
            catch (ModelUnavailableException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        }).RequireInternalKey().WithTags("Search");

        app.MapPost("/api/search/hybrid", async (
            HybridSearchRequest req,
            HybridSearchService hybridSearch,
            IConceptTagRepository conceptTagRepo,
            SessionService session) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new ErrorResponse("Query is required"));

            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (!Enum.TryParse<SearchMode>(req.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new ErrorResponse($"Unknown mode '{req.Mode}'. Expected one of: keyword, semantic, hybrid."));

            try
            {
                var results = await hybridSearch.SearchAsync(req.Query, mode, Math.Clamp(req.TopK, 1, 100));

                var articleResponses = new List<ArticleResponse>();
                var articleIds = results.Select(a => a.Id).ToList();
                var tagMap = await conceptTagRepo.GetByArticleIdsAsync(articleIds);
                foreach (var a in results)
                {
                    var conceptTags = tagMap.GetValueOrDefault(a.Id, new List<string>());
                    articleResponses.Add(ArticleResponse.From(a, conceptTags));
                }
                return Results.Ok(articleResponses);
            }
            catch (ModelUnavailableException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        }).RequireInternalKey().WithTags("Search");
    }
}

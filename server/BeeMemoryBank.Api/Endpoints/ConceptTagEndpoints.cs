using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace BeeMemoryBank.Api.Endpoints;

public static class ConceptTagEndpoints
{
    public static void MapConceptTagEndpoints(this WebApplication app)
    {
        // GET /api/concept-tags?q=...&limit=N — list concept tags; optional substring filter
        app.MapGet("/api/concept-tags", async (HttpContext ctx, ConceptTagService conceptTagService, IConceptTagRepository conceptTagRepo, string? q = null, int limit = 500) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            limit = Math.Clamp(limit, 1, 500);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var filtered = await conceptTagService.ListAsync(q, limit);
                return Results.Ok(filtered.Select(t => new { name = t.Name, articleCount = t.ArticleCount }));
            }

            var tags = await conceptTagRepo.GetAllAsync();
            return Results.Ok(tags.Select(t => new { name = t.Name, articleCount = t.ArticleCount }));
        }).WithTags("ConceptTags");

        // GET /api/concept-tags/graph — get concept tag connections for graph visualization
        app.MapGet("/api/concept-tags/graph", async (HttpContext ctx, IConceptTagRepository conceptTagRepo, IMemoryCache cache) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var userKey = ctx.User.Identity?.Name ?? "anon";
            var cacheKey = $"concept_graph_{userKey}";
            if (!cache.TryGetValue(cacheKey, out List<ConceptGraphEdge>? edges) || edges == null)
            {
                edges = await conceptTagRepo.GetGraphDataAsync();
                cache.Set(cacheKey, edges, TimeSpan.FromMinutes(5));
            }
            return Results.Ok(edges.Select(e => new { source = e.Source, target = e.Target, weight = e.Weight }));
        }).WithTags("ConceptTags");

        // GET /api/concept-tags/graph/home — home graph view with base + pulse node groups
        app.MapGet("/api/concept-tags/graph/home", async (HttpContext ctx, IConceptTagRepository conceptTagRepo, IMemoryCache cache) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var userKey = ctx.User.Identity?.Name ?? "anon";
            var cacheKey = $"concept_graph_home_{userKey}";
            if (!cache.TryGetValue(cacheKey, out ConceptTagGraphData? data) || data == null)
            {
                data = await conceptTagRepo.GetHomeGraphAsync();
                cache.Set(cacheKey, data, TimeSpan.FromMinutes(5));
            }
            return Results.Ok(new
            {
                nodes = data.Nodes.Select(n => new { name = n.Name, articleCount = n.ArticleCount, group = n.Group, totalNeighbors = n.TotalNeighbors }),
                edges = data.Edges.Select(e => new { source = e.Source, target = e.Target, weight = e.Weight })
            });
        }).WithTags("ConceptTags");

        // GET /api/concept-tags/graph/search?q=...&depth=1..3&maxNodes=... — search graph with BFS expansion
        app.MapGet("/api/concept-tags/graph/search", async (HttpContext ctx, IConceptTagRepository conceptTagRepo, string? q, int depth = 2, int maxNodes = 100) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new ErrorResponse("q is required"));

            depth = Math.Clamp(depth, 1, 3);
            maxNodes = Math.Clamp(maxNodes, 10, 500);

            var data = await conceptTagRepo.SearchGraphAsync(q, depth, maxNodes);
            return Results.Ok(new
            {
                nodes = data.Nodes.Select(n => new { name = n.Name, articleCount = n.ArticleCount, group = n.Group, totalNeighbors = n.TotalNeighbors }),
                edges = data.Edges.Select(e => new { source = e.Source, target = e.Target, weight = e.Weight })
            });
        }).WithTags("ConceptTags");

        // GET /api/concept-tags/graph/neighbors — get neighbors for a specific concept tag
        app.MapGet("/api/concept-tags/graph/neighbors", async (string tag, HttpContext ctx, IConceptTagRepository conceptTagRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(tag))
                return Results.BadRequest(new ErrorResponse("tag is required"));

            var edges = await conceptTagRepo.GetNeighborGraphAsync(tag);
            var nodeNames = edges.SelectMany(e => new[] { e.Source, e.Target }).Distinct().ToList();
            return Results.Ok(new
            {
                nodes = nodeNames,
                edges = edges.Select(e => new { source = e.Source, target = e.Target, weight = e.Weight })
            });
        }).WithTags("ConceptTags");

        // GET /api/articles/{id}/concept-tags — get concept tags for an article
        app.MapGet("/api/articles/{id:guid}/concept-tags", async (Guid id, HttpContext ctx, IConceptTagRepository conceptTagRepo, ArticleService articleService) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var article = await articleService.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);
            return Results.Ok(new { conceptTags });
        }).WithTags("ConceptTags");

        // GET /api/concept-tags/{name}/articles — get articles by concept tag
        app.MapGet("/api/concept-tags/{name}/articles", async (string name, HttpContext ctx, ConceptTagService conceptTagService) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new ErrorResponse("Name required"));

            var articles = await conceptTagService.SearchByConceptAsync(name);

            return Results.Ok(new { articles = articles.Select(a => new { id = a.Id, title = a.Title, treePath = a.TreePath }) });
        }).WithTags("ConceptTags");

        // PUT /api/articles/{id}/concept-tags — set concept tags for an article
        app.MapPut("/api/articles/{id:guid}/concept-tags", async (Guid id, HttpContext ctx, ConceptTagService conceptTagService, IConceptTagRepository conceptTagRepo, ArticleService articleService, SetConceptTagsRequest req) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var article = await articleService.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            await conceptTagService.SetForArticleAsync(id, req.ConceptTags);
            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);
            return Results.Ok(new { conceptTags });
        }).WithTags("ConceptTags");

        // GET /api/articles/{id}/related — get related articles via shared concept tags
        app.MapGet("/api/articles/{id:guid}/related", async (Guid id, HttpContext ctx, IConceptTagRepository conceptTagRepo, ArticleService articleService) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var article = await articleService.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var related = await conceptTagRepo.GetRelatedArticlesAsync(id);
            return Results.Ok(related.Select(r => new
            {
                id = r.Id,
                title = r.Title,
                treePath = r.TreePath,
                sharedConcepts = r.SharedConcepts,
                strength = r.Strength
            }));
        }).WithTags("ConceptTags");

        // PUT /api/concept-tags/{name} — rename globally
        app.MapPut("/api/concept-tags/{name}", async (string name, RenameTagRequest req, HttpContext ctx, ConceptTagService conceptTagService) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var caller = CallerIdentity.Extract(ctx);
            if (!caller.IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.NewName))
                return Results.BadRequest(new ErrorResponse("newName must not be empty"));

            try
            {
                await conceptTagService.RenameAsync(name, req.NewName);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
        }).WithTags("ConceptTags");

        // POST /api/concept-tags/merge — merge source into target
        app.MapPost("/api/concept-tags/merge", async (MergeConceptTagRequest req, HttpContext ctx, ConceptTagService conceptTagService) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var caller = CallerIdentity.Extract(ctx);
            if (!caller.IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Target))
                return Results.BadRequest(new ErrorResponse("source and target must not be empty"));

            try
            {
                await conceptTagService.MergeAsync(req.Source, req.Target);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
        }).WithTags("ConceptTags");

        // DELETE /api/concept-tags/{name} — delete globally
        app.MapDelete("/api/concept-tags/{name}", async (string name, HttpContext ctx, ConceptTagService conceptTagService) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var caller = CallerIdentity.Extract(ctx);
            if (!caller.IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            try
            {
                await conceptTagService.DeleteAsync(name);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new ErrorResponse(ex.Message));
            }
        }).WithTags("ConceptTags");
    }
}

public record SetConceptTagsRequest(List<string> ConceptTags);
public record MergeConceptTagRequest(string Source, string Target);
public record RenameTagRequest(string NewName);

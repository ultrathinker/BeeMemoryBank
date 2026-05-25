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
            // ETag mirrors LamportTs so clients can send If-Match on the next PUT
            // and get a 409 on concurrent edits (Phase 4 conflict-resolution).
            ctx.Response.Headers["ETag"] = $"\"v{article.LamportTs}\"";
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
                var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, req.TreePath))
                    return Results.Json(new ErrorResponse($"You don't have permission to create an article in {PathHelper.Display(req.TreePath)}."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, req.TreePath))
                    return Results.Json(new ErrorResponse($"Folder {PathHelper.Display(req.TreePath)} is read-only for your user."), statusCode: 403);
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

            var existingMeta = await svc.GetMetadataAsync(id);
            if (existingMeta == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            // SECURITY ORDER: ACL gate FIRST, optimistic concurrency SECOND.
            // The 409 body contains currentContent (plaintext) so we must NOT
            // serve it before confirming the caller can read this article —
            // otherwise anyone could probe arbitrary GUIDs and exfiltrate content
            // through forced version-mismatch responses. (kilo security review)
            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, existingMeta.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, existingMeta.TreePath))
                    return Results.Json(new ErrorResponse("This article is in a read-only folder for your user."), statusCode: 403);

                if (req.TreePath != null)
                {
                    if (FolderAccessService.IsAccessDenied(folderPaths, policy, req.TreePath))
                        return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);
                    if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, req.TreePath))
                        return Results.Json(new ErrorResponse($"Target folder {PathHelper.Display(req.TreePath)} is read-only for your user."), statusCode: 403);
                }
            }

            // Optimistic concurrency: If-Match header carries the LamportTs the
            // client based its edit on. Mismatch → 409 with the current server state
            // so the friend's UI can offer "force overwrite" or "save as draft".
            var ifMatch = ctx.Request.Headers.IfMatch.FirstOrDefault();
            if (!string.IsNullOrEmpty(ifMatch))
            {
                var expected = ifMatch.Trim('"', ' ');
                if (expected.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    expected = expected[1..];
                // Fail-closed on malformed If-Match so a client can't bypass
                // optimistic concurrency by sending garbage (kilo/gemini review).
                if (!long.TryParse(expected, out var expectedVersion))
                    return Results.BadRequest(new ErrorResponse(
                        "Invalid If-Match header (expected ETag like \"v12345\")."));
                if (expectedVersion != existingMeta.LamportTs)
                {
                    string? currentContent = null;
                    try { currentContent = await svc.GetContentAsync(id); } catch { /* maybe locked */ }
                    return Results.Json(new
                    {
                        error = "VersionMismatch",
                        currentVersion = existingMeta.LamportTs,
                        currentTitle = existingMeta.Title,
                        currentTreePath = existingMeta.TreePath,
                        currentUpdatedAt = existingMeta.UpdatedAt,
                        currentContent
                    }, statusCode: 409);
                }
            }

            await svc.UpdateAsync(id, req.Title, req.TreePath, null, req.Content);
            if (req.ConceptTags != null)
                await conceptTagSvc.SetForArticleAsync(id, req.ConceptTags);
            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));
            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);

            // ETag echoes the new LamportTs so subsequent edits can match it.
            ctx.Response.Headers["ETag"] = $"\"v{article.LamportTs}\"";
            return Results.Ok(ArticleResponse.From(article, conceptTags));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ArticleService svc, HttpContext ctx, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing != null)
                {
                    if (FolderAccessService.IsAccessDenied(folderPaths, policy, existing.TreePath))
                        return Results.Json(new ErrorResponse("You don't have permission to delete this article."), statusCode: 403);
                    if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, existing.TreePath))
                        return Results.Json(new ErrorResponse("This article is in a read-only folder for your user."), statusCode: 403);
                }
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
                var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                var existing = await svc.GetMetadataAsync(id);
                if (existing == null)
                    return Results.NotFound(new ErrorResponse($"Article {id} not found"));

                if (FolderAccessService.IsAccessDenied(folderPaths, policy, existing.TreePath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, existing.TreePath))
                    return Results.Json(new ErrorResponse("Source folder is read-only for your user."), statusCode: 403);

                if (FolderAccessService.IsAccessDenied(folderPaths, policy, req.NewPath))
                    return Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, req.NewPath))
                    return Results.Json(new ErrorResponse($"Target folder {PathHelper.Display(req.NewPath)} is read-only for your user."), statusCode: 403);
            }

            await svc.MoveAsync(id, req.NewPath);
            return Results.Ok(new MoveArticleResponse(id, req.NewPath));
        });
    }
}

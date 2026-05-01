using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/comments", async (
            CommentService commentSvc,
            ArticleService articleSvc,
            SessionService session,
            HttpContext ctx,
            Guid articleId) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var comments = await commentSvc.GetDecryptedByArticleAsync(articleId);
            var result = comments.Select(c => new CommentResponse(c.comment.Id, c.comment.ArticleId, c.text, c.comment.CreatedAt));
            return Results.Ok(result);
        }).WithTags("Comments");

        app.MapPost("/api/comments", async (
            CommentService commentSvc,
            ArticleService articleSvc,
            SessionService session,
            FolderAccessService folderAccess,
            AddCommentRequest req,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new ErrorResponse("Text is required"));
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session locked"), statusCode: 403);

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var article = await articleSvc.GetMetadataAsync(req.ArticleId);
                if (article is null)
                    return Results.NotFound(new ErrorResponse($"Article {req.ArticleId} not found"));
                var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                    return Results.Json(new ErrorResponse($"Access denied for article {req.ArticleId}."), statusCode: 403);
            }

            var comment = await commentSvc.CreateAsync(req.ArticleId, req.Text.Trim());
            var text = await commentSvc.DecryptTextAsync(comment);
            return Results.Ok(new CommentResponse(comment.Id, comment.ArticleId, text, comment.CreatedAt));
        }).WithTags("Comments");

        app.MapDelete("/api/comments/{id:int}", async (
            CommentService commentSvc,
            ICommentRepository commentRepo,
            ArticleService articleSvc,
            FolderAccessService folderAccess,
            int id,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var comment = await commentRepo.GetByIdAsync(id);
            if (comment == null)
                return Results.NotFound(new ErrorResponse($"Comment {id} not found"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var article = await articleSvc.GetMetadataAsync(comment.ArticleId);
                if (article is null)
                    return Results.NotFound(new ErrorResponse($"Comment {id} not found"));
                var (folderPaths, policy) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                    return Results.Json(new ErrorResponse($"Access denied for comment {id}."), statusCode: 403);
            }

            await commentSvc.DeleteAsync(id);
            return Results.NoContent();
        }).WithTags("Comments");
    }
}

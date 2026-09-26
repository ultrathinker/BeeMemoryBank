using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/comments", async (
            CommentService commentSvc,
            ArticleService articleSvc,
            SessionService session,
            FolderAccessService folderAccess,
            ProtectedUnlockCache unlockCache,
            HttpContext ctx,
            Guid articleId) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var article = await articleSvc.GetMetadataAsync(articleId);
            if (article is null)
                return Results.NotFound(new ErrorResponse($"Article {articleId} not found"));

            // The same read ACL as the article itself: this listed any article's comments to any
            // caller, including a user denied the folder.
            var (userId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (folderPaths, policy, _) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                    return Results.Json(new ErrorResponse($"Access denied for article {articleId}."), statusCode: 403);
            }

            // A protected article's comments open only for a caller who unlocked the article.
            var passphrase = article.Protected ? unlockCache.TryGet(ArticleEndpoints.CallerKey(ctx), articleId) : null;
            var comments = await commentSvc.GetOpenedByArticleAsync(articleId, passphrase);
            var result = comments.Select(c => new CommentResponse(c.Comment.Id, c.Comment.ArticleId, c.Text, c.Comment.CreatedAt, c.Locked));
            return Results.Ok(result);
        }).RequireInternalKey().WithTags("Comments");

        app.MapPost("/api/comments", async (
            CommentService commentSvc,
            ArticleService articleSvc,
            SessionService session,
            FolderAccessService folderAccess,
            ProtectedUnlockCache unlockCache,
            AddCommentRequest req,
            HttpContext ctx) =>
        {
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
                var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                    return Results.Json(new ErrorResponse($"Access denied for article {req.ArticleId}."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, article.TreePath))
                    return Results.Json(new ErrorResponse($"Article {req.ArticleId} is in a read-only folder."), statusCode: 403);
            }

            // On a protected article the comment is sealed under its passphrase, taken from this
            // caller's unlock of the article; not unlocked -> refused (ArticleLockedException).
            var passphrase = unlockCache.TryGet(ArticleEndpoints.CallerKey(ctx), req.ArticleId);
            try
            {
                var comment = await commentSvc.CreateAsync(req.ArticleId, req.Text.Trim(), passphrase);
                var text = await commentSvc.DecryptTextAsync(comment, passphrase);
                return Results.Ok(new CommentResponse(comment.Id, comment.ArticleId, text, comment.CreatedAt));
            }
            catch (ArticleLockedException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message, "ArticleLocked"), statusCode: 403);
            }
        }).RequireInternalKey().WithTags("Comments");

        app.MapDelete("/api/comments/{id:int}", async (
            CommentService commentSvc,
            ICommentRepository commentRepo,
            ArticleService articleSvc,
            FolderAccessService folderAccess,
            int id,
            HttpContext ctx) =>
        {

            var comment = await commentRepo.GetByIdAsync(id);
            if (comment == null)
                return Results.NotFound(new ErrorResponse($"Comment {id} not found"));

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var article = await articleSvc.GetMetadataAsync(comment.ArticleId);
                if (article is null)
                    return Results.NotFound(new ErrorResponse($"Comment {id} not found"));
                var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(folderPaths, policy, article.TreePath))
                    return Results.Json(new ErrorResponse($"Access denied for comment {id}."), statusCode: 403);
                if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, article.TreePath))
                    return Results.Json(new ErrorResponse($"Comment {id}'s article is in a read-only folder."), statusCode: 403);
            }

            await commentSvc.DeleteAsync(id);
            return Results.NoContent();
        }).RequireInternalKey().WithTags("Comments");
    }
}

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Endpoints;

public static class VersionEndpoints
{
    public static void MapVersionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/articles").WithTags("Versions");

        group.MapGet("/{id:guid}/versions", async (Guid id, HttpContext ctx, IArticleVersionRepository versionRepo, ArticleService svc, FolderAccessService folderAccess) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

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

            var versions = await versionRepo.GetByArticleIdAsync(id);
            return Results.Ok(versions.Select(v => new
            {
                id = v.Id,
                versionNumber = v.VersionNumber,
                title = v.Title,
                tags = v.Tags,
                treePath = v.TreePath,
                updatedBy = v.UpdatedBy,
                createdAt = v.CreatedAt
            }));
        });

        group.MapGet("/{id:guid}/versions/{versionNumber:int}", async (Guid id, int versionNumber, HttpContext ctx, IArticleVersionRepository versionRepo, SessionService session, ArticleService svc, FolderAccessService folderAccess) =>
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

            var version = await versionRepo.GetAsync(id, versionNumber);
            if (version == null)
                return Results.NotFound(new ErrorResponse($"Version {versionNumber} not found for article {id}"));

            var masterDek = session.GetMasterDek();
            try
            {
                var articleDek = DekManager.UnwrapDek(version.EncryptedDek, version.DekIV, masterDek);
                var content = ArticleEncryptor.Decrypt(version.Ciphertext, version.IV, articleDek);
                Array.Clear(articleDek);

                return Results.Ok(new
                {
                    id = version.Id,
                    versionNumber = version.VersionNumber,
                    title = version.Title,
                    tags = version.Tags,
                    treePath = version.TreePath,
                    content,
                    createdAt = version.CreatedAt
                });
            }
            finally
            {
                Array.Clear(masterDek);
            }
        });
    }
}

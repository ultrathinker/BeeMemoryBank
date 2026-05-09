using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Endpoints;

public static class VersionEndpoints
{
    public static void MapVersionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/articles").WithTags("Versions");

        group.MapGet("/{id:guid}/versions", async (Guid id, HttpContext ctx, IArticleVersionRepository versionRepo, ArticleService svc) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var versions = await versionRepo.GetByArticleIdAsync(id);
            return Results.Ok(versions.Select(v => new
            {
                id = v.Id,
                versionNumber = v.VersionNumber,
                title = v.Title,
                treePath = v.TreePath,
                updatedBy = v.UpdatedBy,
                createdAt = v.CreatedAt
            }));
        });

        group.MapGet("/{id:guid}/versions/{versionNumber:int}", async (Guid id, int versionNumber, HttpContext ctx, IArticleVersionRepository versionRepo, SessionService session, ArticleService svc) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var version = await versionRepo.GetAsync(id, versionNumber);
            if (version == null)
                return Results.NotFound(new ErrorResponse($"Version {versionNumber} not found for article {id}"));

            var masterDek = session.GetMasterDek();
            try
            {
                var isV1 = version.EncryptedDek.Length > 48 && version.EncryptedDek[0] == 0x01;
                var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
                var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
                var articleDek = DekManager.UnwrapDek(version.EncryptedDek, version.DekIV, masterDek, dekAad);
                var content = ArticleEncryptor.Decrypt(version.Ciphertext, version.IV, articleDek, bodyAad);
                Array.Clear(articleDek);

                return Results.Ok(new
                {
                    id = version.Id,
                    versionNumber = version.VersionNumber,
                    title = version.Title,
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

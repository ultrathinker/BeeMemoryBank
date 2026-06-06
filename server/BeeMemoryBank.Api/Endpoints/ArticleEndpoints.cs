using System.Security.Cryptography;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
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
                // Create-with-password: wrap the body BEFORE the first save so the plaintext never
                // reaches the event log or sync. The very first CREATE event carries only ciphertext.
                var content = req.Content;
                string? hint = null;
                if (!string.IsNullOrEmpty(req.Passphrase))
                {
                    if (req.Passphrase.Length < 4)
                        return Results.Json(new ErrorResponse("Passphrase must be at least 4 characters."), statusCode: 400);
                    content = BeeMemoryBank.Crypto.ProtectedContentCodec.Wrap(req.Content, req.Passphrase);
                    hint = string.IsNullOrWhiteSpace(req.Hint) ? null : req.Hint.Trim();
                }
                var article = await svc.CreateAsync(req.Title, req.TreePath, [], content, hint);
                if (req.ConceptTags is { Count: > 0 })
                    await conceptTagSvc.SetForArticleAsync(article.Id, req.ConceptTags);
                return Results.Created($"/api/articles/{article.Id}", ArticleResponse.From(article, req.ConceptTags ?? []));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse($"You don't have permission to create an article in {PathHelper.Display(req.TreePath)}."), statusCode: 403);
            }
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateArticleRequest req, ArticleService svc, ConceptTagService conceptTagSvc, IConceptTagRepository conceptTagRepo, SessionService session, HttpContext ctx, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
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

            if (req.Content != null && existingMeta.Protected)
            {
                // Editing a protected article's body: re-wrap the new content under its passphrase.
                // The passphrase comes from the request OR, for a seamless read→edit handoff, from the
                // server-side recent-unlock cache (so the browser never has to hold or resend it).
                var passphrase = !string.IsNullOrEmpty(req.Passphrase)
                    ? req.Passphrase
                    : unlockCache.TryGet(CallerKey(ctx), id);
                if (string.IsNullOrEmpty(passphrase))
                    return Results.Json(new ErrorResponse("This article is protected — a passphrase is required to edit its content."), statusCode: 400);
                // Body FIRST: UpdateProtectedContentAsync verifies the passphrase before writing, so a
                // wrong passphrase aborts with nothing committed. Only then apply title/path — otherwise
                // a bad passphrase would still have persisted the metadata change (non-atomic edit).
                try
                {
                    await svc.UpdateProtectedContentAsync(id, req.Content, passphrase);
                }
                catch (CryptographicException)
                {
                    return Results.Json(new ErrorResponse("Wrong passphrase."), statusCode: 401);
                }
                if (req.Title != null || req.TreePath != null)
                    await svc.UpdateAsync(id, req.Title, req.TreePath, null, null);
                // Refresh the cache window so consecutive saves keep working without re-prompting.
                unlockCache.Remember(CallerKey(ctx), id, passphrase);
            }
            else
            {
                await svc.UpdateAsync(id, req.Title, req.TreePath, null, req.Content);
            }
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

        // ===== Second-layer ("protected article") encryption =====

        group.MapPost("/{id:guid}/protect", async (Guid id, ProtectArticleRequest req, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
            var (_, error) = await WriteGateAsync(id, ctx, svc, session, folderAccess);
            if (error != null) return error;
            if (string.IsNullOrEmpty(req.Passphrase) || req.Passphrase.Length < 4)
                return Results.Json(new ErrorResponse("Passphrase must be at least 4 characters."), statusCode: 400);
            try
            {
                await svc.ProtectAsync(id, req.Passphrase, string.IsNullOrWhiteSpace(req.Hint) ? null : req.Hint.Trim());
                // The caller just proved the passphrase — prime the cache so editing right after
                // protecting doesn't immediately re-prompt.
                unlockCache.Remember(CallerKey(ctx), id, req.Passphrase);
                return Results.Ok(new { protected_ = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        group.MapPost("/{id:guid}/unprotect", async (Guid id, UnprotectArticleRequest req, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
            var (_, error) = await WriteGateAsync(id, ctx, svc, session, folderAccess);
            if (error != null) return error;
            try
            {
                await svc.UnprotectAsync(id, req.Passphrase);
                unlockCache.Forget(CallerKey(ctx), id); // no longer protected
                return Results.Ok(new { protected_ = false });
            }
            catch (CryptographicException)
            {
                return Results.Json(new ErrorResponse("Wrong passphrase."), statusCode: 401);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        group.MapPost("/{id:guid}/change-passphrase", async (Guid id, ChangeArticlePassphraseRequest req, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
            var (_, error) = await WriteGateAsync(id, ctx, svc, session, folderAccess);
            if (error != null) return error;
            if (string.IsNullOrEmpty(req.NewPassphrase) || req.NewPassphrase.Length < 4)
                return Results.Json(new ErrorResponse("New passphrase must be at least 4 characters."), statusCode: 400);
            try
            {
                await svc.ChangePassphraseAsync(id, req.OldPassphrase, req.NewPassphrase, string.IsNullOrWhiteSpace(req.Hint) ? null : req.Hint.Trim());
                unlockCache.Remember(CallerKey(ctx), id, req.NewPassphrase); // cache the new passphrase
                return Results.Ok(new { changed = true });
            }
            catch (CryptographicException)
            {
                return Results.Json(new ErrorResponse("Wrong current passphrase."), statusCode: 401);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        // Read-only unlock: returns the decrypted plaintext. Uses READ ACL so read-only-folder users
        // can still view a protected article they have access to.
        group.MapPost("/{id:guid}/unlock", async (Guid id, UnlockArticleRequest req, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

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

            try
            {
                var content = await svc.UnlockContentAsync(id, req.Passphrase);
                // Remember the verified passphrase server-side (60s) so the Edit page can open without
                // re-prompting. Never returned to the browser. View page load does NOT read this.
                if (meta.Protected)
                    unlockCache.Remember(CallerKey(ctx), id, req.Passphrase);
                return Results.Ok(new ArticleContentResponse(id, content));
            }
            catch (CryptographicException)
            {
                return Results.Json(new ErrorResponse("Wrong passphrase."), statusCode: 401);
            }
        });

        // Edit-load helper: tells the Edit page whether the article is protected and, if it was
        // unlocked in the last 60s by this caller, returns the decrypted body so the editor opens
        // without a re-prompt. On a cache miss it returns unlocked=false (the Edit page shows the gate).
        group.MapGet("/{id:guid}/edit-content", async (Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

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

            if (!meta.Protected)
            {
                var plain = await svc.GetContentAsync(id);
                return Results.Ok(new EditContentResponse(id, false, true, plain));
            }

            var cached = unlockCache.TryGet(CallerKey(ctx), id);
            if (cached == null)
                return Results.Ok(new EditContentResponse(id, true, false, null));
            try
            {
                var content = await svc.UnlockContentAsync(id, cached);
                return Results.Ok(new EditContentResponse(id, true, true, content));
            }
            catch (CryptographicException)
            {
                // Cached passphrase no longer valid (e.g. changed elsewhere) — force a re-prompt.
                unlockCache.Forget(CallerKey(ctx), id);
                return Results.Ok(new EditContentResponse(id, true, false, null));
            }
        });
    }

    // Stable per-caller key for the unlock cache so one user's unlock can't be reused by another.
    private static string CallerKey(HttpContext ctx)
    {
        var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
        return $"u{userId}:a{agentId}:s{isSuperadmin}";
    }

    // Shared gate for protected-article WRITE operations: internal key + unlocked session + write ACL.
    // Returns the metadata on success, or the IResult to return on failure.
    private static async Task<(Article? meta, IResult? error)> WriteGateAsync(
        Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess)
    {
        if (!InternalKeyValidator.Validate(ctx))
            return (null, Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403));
        if (!session.IsUnlocked)
            return (null, Results.Json(new ErrorResponse("Session is locked"), statusCode: 403));

        var meta = await svc.GetMetadataAsync(id);
        if (meta == null)
            return (null, Results.NotFound(new ErrorResponse($"Article {id} not found")));

        var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
        if (!isSuperadmin)
        {
            var (folderPaths, policy, readOnlyPaths) = await folderAccess.GetFullAccessInfoAsync(userId);
            if (FolderAccessService.IsAccessDenied(folderPaths, policy, meta.TreePath))
                return (null, Results.Json(new ErrorResponse("You don't have permission to modify this article."), statusCode: 403));
            if (FolderAccessService.IsReadOnlyForCaller(readOnlyPaths, meta.TreePath))
                return (null, Results.Json(new ErrorResponse("This article is in a read-only folder for your user."), statusCode: 403));
        }
        return (meta, null);
    }
}

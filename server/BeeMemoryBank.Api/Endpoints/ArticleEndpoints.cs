using System.Security.Cryptography;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Endpoints;

public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/articles").WithTags("Articles").RequireInternalKey();

        group.MapGet("/", async (HttpContext ctx, ArticleService svc, IConceptTagRepository conceptTagRepo, string? treePath = null) =>
        {

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

            var article = await svc.GetMetadataAsync(id);
            if (article == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);
            // ETag mirrors LamportTs so clients can send If-Match on the next PUT
            // and get a 409 on concurrent edits (conflict-resolution).
            ctx.Response.Headers["ETag"] = $"\"v{article.LamportTs}\"";
            return Results.Ok(ArticleResponse.From(article, conceptTags));
        });

        group.MapGet("/{id:guid}/content", async (Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess) =>
        {
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
                var attachmentIds = req.AttachmentIds ?? [];
                if (!string.IsNullOrEmpty(req.Passphrase) && attachmentIds.Count > 0)
                    return Results.Json(new ErrorResponse("A password-protected article can't have attachments. Remove the files or create it without a password."), statusCode: 400);
                if (!string.IsNullOrEmpty(req.Passphrase))
                {
                    if (req.Passphrase.Length < 4)
                        return Results.Json(new ErrorResponse("Passphrase must be at least 4 characters."), statusCode: 400);
                    content = BeeMemoryBank.Crypto.ProtectedContentCodec.Wrap(req.Content, req.Passphrase);
                    hint = string.IsNullOrWhiteSpace(req.Hint) ? null : req.Hint.Trim();
                }
                // Hand the tags to CreateAsync rather than attaching them afterwards. CreateAsync
                // sets them inside its own transaction and reads them back through it, so the
                // article_create event carries them to every peer. Setting them after the call
                // stored them locally and emitted nothing — the article arrived on other nodes
                // with an empty tag set, permanently.
                var article = await svc.CreateAsync(req.Title, req.TreePath, req.ConceptTags ?? [], content, hint);
                // Only files this caller uploaded (a superadmin already sees every unlinked row); a
                // non-superadmin with no identity to match on links nothing.
                var uploader = CallerIdentity.Extract(ctx).MediaOwnerKey;
                if (attachmentIds.Count > 0 && (isSuperadmin || uploader != null))
                    await svc.LinkAttachmentsAsync(article.Id, attachmentIds, isSuperadmin ? null : uploader);
                return Results.Created($"/api/articles/{article.Id}", ArticleResponse.From(article, req.ConceptTags ?? []));
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteAclDenial.TryClassify(ex, out var kind, out var path);
                var message = kind == WriteAclDenialKind.ReadOnly
                    ? $"Folder {PathHelper.Display(path)} is read-only for your user."
                    : $"You don't have permission to create an article in {PathHelper.Display(req.TreePath)}.";
                return Results.Json(new ErrorResponse(message), statusCode: 403);
            }
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateArticleRequest req, ArticleService svc, ConceptTagService conceptTagSvc, IConceptTagRepository conceptTagRepo, SessionService session, HttpContext ctx, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
            if (req.Content != null && !session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var existingMeta = await svc.GetMetadataAsync(id);
            if (existingMeta == null)
                return Results.NotFound(new ErrorResponse($"Article {id} not found"));

            // SECURITY ORDER: ACL gate FIRST, optimistic concurrency SECOND.
            // The 409 body contains currentContent (plaintext) so we must NOT
            // serve it before confirming the caller can read this article —
            // otherwise anyone could probe arbitrary GUIDs and exfiltrate content
            // through forced version-mismatch responses.
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
                // optimistic concurrency by sending garbage.
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
                if (ThrottlePassphraseAttempt(ctx, id) is { } throttled) return throttled;
                try
                {
                    await svc.UpdateProtectedContentAsync(id, req.Content, passphrase);
                    PassphraseAttemptSucceeded(ctx, id);
                }
                catch (CryptographicException)
                {
                    return Results.Json(new ErrorResponse("Wrong passphrase."), statusCode: 401);
                }
                if (req.Title != null || req.TreePath != null || req.ConceptTags != null)
                    await svc.UpdateAsync(id, req.Title, req.TreePath, req.ConceptTags, null);
                // Refresh the cache window so consecutive saves keep working without re-prompting.
                unlockCache.Remember(CallerKey(ctx), id, passphrase);
            }
            else
            {
                await svc.UpdateAsync(id, req.Title, req.TreePath, req.ConceptTags, req.Content);
            }
            // Tags go through UpdateAsync (above), never a bare SetForArticleAsync afterwards:
            // only the service path puts them in the article_update event, and only that reaches peers.
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
            if (ThrottlePassphraseAttempt(ctx, id) is { } throttled) return throttled;
            try
            {
                await svc.UnprotectAsync(id, req.Passphrase);
                PassphraseAttemptSucceeded(ctx, id);
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
            if (ThrottlePassphraseAttempt(ctx, id) is { } throttled) return throttled;
            try
            {
                await svc.ChangePassphraseAsync(id, req.OldPassphrase, req.NewPassphrase, string.IsNullOrWhiteSpace(req.Hint) ? null : req.Hint.Trim());
                PassphraseAttemptSucceeded(ctx, id);
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

            if (ThrottlePassphraseAttempt(ctx, id) is { } throttled) return throttled;
            try
            {
                var content = await svc.UnlockContentAsync(id, req.Passphrase);
                PassphraseAttemptSucceeded(ctx, id);
                // Remember the verified passphrase server-side (see ProtectedUnlockCache.Ttl) so the
                // View and Edit pages can both open without re-prompting. Never returned to the browser.
                // No caller key (browser request without a Web session) → nothing is cached, and the
                // page gets no countdown because the next load will prompt again anyway.
                int? expiresIn = null;
                var callerKey = CallerKey(ctx);
                if (meta.Protected && callerKey != null)
                    expiresIn = SecondsUntil(unlockCache.Remember(callerKey, id, req.Passphrase));
                return Results.Ok(new UnlockArticleResponse(id, content, expiresIn));
            }
            catch (CryptographicException)
            {
                return Results.Json(new ErrorResponse("Wrong passphrase."), statusCode: 401);
            }
        });

        // Explicit re-lock: drops this caller's unlock-cache entry early, before its TTL would
        // otherwise expire it on its own. Needed because View/edit-content now trust that cache —
        // without this, clicking "Re-lock" would only hide the content client-side, and the very
        // next reload would show it unlocked again for the rest of the TTL.
        group.MapPost("/{id:guid}/relock", (Guid id, HttpContext ctx, SessionService session, ProtectedUnlockCache unlockCache) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            unlockCache.Forget(CallerKey(ctx), id);
            return Results.Ok(new { relocked = true });
        });

        // Cache-aware content load: tells the caller whether the article is protected and, if it was
        // unlocked recently by this caller (within ProtectedUnlockCache.Ttl), returns the decrypted
        // body so the View/Edit pages open without a re-prompt. Used by both. On a cache miss it
        // returns unlocked=false (the page shows the passphrase gate).
        group.MapGet("/{id:guid}/edit-content", async (Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess, ProtectedUnlockCache unlockCache) =>
        {
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

            var cached = unlockCache.TryGet(CallerKey(ctx), id, out var expiresUtc);
            if (cached == null)
                return Results.Ok(new EditContentResponse(id, true, false, null));
            try
            {
                var content = await svc.UnlockContentAsync(id, cached);
                return Results.Ok(new EditContentResponse(id, true, true, content, SecondsUntil(expiresUtc)));
            }
            catch (CryptographicException)
            {
                // Cached passphrase no longer valid (e.g. changed elsewhere) — force a re-prompt.
                unlockCache.Forget(CallerKey(ctx), id);
                return Results.Ok(new EditContentResponse(id, true, false, null));
            }
        });
    }

    // Stable per-caller key for the unlock cache so one caller's unlock can't be reused by another.
    // A browser caller (Web proxy, internal key) is further scoped to its own login session: the Web
    // app mints a random id per sign-in and forwards it as X-Web-Session (see InternalKeyHandler).
    // Without it, the same user signed in on a second device or browser would see the article open
    // there too. A non-agent request that lacks the header gets no key at all — the cache is then
    // bypassed and the user is re-prompted, rather than falling back to a user-wide entry.
    // Every passphrase check runs a full Argon2id derivation before the GCM tag can say "wrong", so
    // unthrottled guessing is both a brute-force channel against the passphrase and a CPU/memory
    // drain on the node. Budget: failed attempts per caller identity per article; a success clears
    // it, so ordinary repeated saves of a protected article never add up. Keyed on the user/agent,
    // not the web session, so signing in again does not reset the budget.
    private static readonly SlidingWindowRateLimiter PassphraseAttempts = new(10, TimeSpan.FromMinutes(5));

    private static string PassphraseAttemptKey(HttpContext ctx, Guid id)
    {
        var (userId, agentId, _) = CallerIdentity.Extract(ctx);
        return $"u{userId}:a{agentId}:{id:N}";
    }

    private static IResult? ThrottlePassphraseAttempt(HttpContext ctx, Guid id) =>
        PassphraseAttempts.TryAcquire(PassphraseAttemptKey(ctx, id))
            ? null
            : Results.Json(new ErrorResponse("Too many passphrase attempts for this article. Try again in a few minutes."),
                statusCode: StatusCodes.Status429TooManyRequests);

    private static void PassphraseAttemptSucceeded(HttpContext ctx, Guid id) =>
        PassphraseAttempts.Reset(PassphraseAttemptKey(ctx, id));

    private static string? CallerKey(HttpContext ctx)
    {
        var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
        var key = $"u{userId}:a{agentId}:s{isSuperadmin}";
        if (agentId != null) return key;

        var webSession = ctx.Request.Headers["X-Web-Session"].FirstOrDefault();
        if (string.IsNullOrEmpty(webSession) || webSession.Length > 128 || !InternalKeyValidator.Validate(ctx))
            return null;
        return $"{key}:w{webSession}";
    }

    private static int SecondsUntil(DateTime utc) =>
        Math.Max(0, (int)Math.Ceiling((utc - DateTime.UtcNow).TotalSeconds));

    // Shared gate for protected-article WRITE operations: internal key + unlocked session + write ACL.
    // Returns the metadata on success, or the IResult to return on failure.
    private static async Task<(Article? meta, IResult? error)> WriteGateAsync(
        Guid id, HttpContext ctx, ArticleService svc, SessionService session, FolderAccessService folderAccess)
    {
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

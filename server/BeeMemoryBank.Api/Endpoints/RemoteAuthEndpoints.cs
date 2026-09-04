using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// Endpoints for issuing and serving cross-instance "remote API tokens".
/// A remote token is exchanged once for a user login (username + password)
/// and then used as a long-lived bearer token (90-day rolling TTL) so the
/// other BMB node can poll mirrored folders without storing the password.
///
/// Read-only by design in Phase 3; Phase 4 adds write-through.
/// </summary>
public static class RemoteAuthEndpoints
{
    public static void MapRemoteAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/remote-token", async (
            RemoteTokenIssueRequest req,
            IUserRepository userRepo,
            IRemoteApiTokenRepository tokenRepo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new ErrorResponse("username and password are required"));

            var user = await userRepo.GetByUsernameAsync(req.Username);
            if (user is null || !user.IsActive)
            {
                // Same Argon2id cost as a real account, so response time does not distinguish
                // "no such user" / "deactivated" from "wrong password". See
                // UserService.BurnPasswordVerification — this endpoint is reachable from other
                // nodes by design, which makes the oracle remotely measurable.
                UserService.BurnPasswordVerification(req.Password);
                return Results.Json(new ErrorResponse("Invalid credentials"), statusCode: 401);
            }

            if (!UserService.VerifyPassword(req.Password, user.PasswordHash))
                return Results.Json(new ErrorResponse("Invalid credentials"), statusCode: 401);

            var token = RemoteTokenHelper.GenerateToken();
            var record = new RemoteApiToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = RemoteTokenHelper.Hash(token),
                Label = req.Label,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(90)
            };
            await tokenRepo.CreateAsync(record);

            // Token is shown ONCE — the client stores it locally, encrypted.
            return Results.Ok(new
            {
                token,
                expiresAt = record.ExpiresAt,
                userId = user.Id,
                username = user.Username,
                displayName = user.DisplayName,
                isSuperadmin = user.Role == UserRoles.Superadmin
            });
        }).WithTags("RemoteAuth");

        app.MapGet("/api/folders/accessible", async (HttpContext ctx,
            IFolderRepository folderRepo,
            IArticleRepository articleRepo,
            FolderAccessService folderAccess,
            SessionService session) =>
        {
            // Auth: this endpoint expects a remote bearer token (CallerIdentity set by middleware).
            var (userId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (userId is null)
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            // Even folder metadata (paths, article counts) is sensitive — refuse
            // to disclose it while the vault is locked. Kilo security review.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Owner session is locked"), statusCode: 423);

            var folders = await folderRepo.GetAllActiveAsync();
            var articles = await articleRepo.ListAsync();

            HashSet<string> denyPaths = [], allowPaths = [], roPaths = [];
            if (!isSuperadmin)
                (denyPaths, allowPaths, roPaths) = await folderAccess.GetFullAccessInfoAsync(userId);

            var result = folders
                .Where(f => isSuperadmin || !FolderAccessService.IsAccessDenied(denyPaths, allowPaths, f.Path))
                .Where(f => f.RemoteSubscriptionId == null) // only local-original folders are mirrorable
                .Select(f => new
                {
                    id = f.Id,
                    path = f.Path,
                    name = f.Name,
                    isReadOnly = !isSuperadmin && FolderAccessService.IsReadOnlyForCaller(roPaths, f.Path),
                    articleCount = articles.Count(a => (a.TreePath ?? "/") == f.Path
                                                    || (a.TreePath ?? "/").StartsWith(f.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                })
                .OrderBy(f => f.path)
                .ToList();

            return Results.Ok(new { folders = result });
        }).WithTags("RemoteAuth");

        // Snapshot endpoint: returns the full state of a folder + descendants.
        // MVP simplification — every friend-side poll re-fetches the snapshot
        // (cheap because article bodies are TEXT already decrypted by the
        // session). Future optimisation: /changes since cursor for diffs only.
        app.MapGet("/api/folders/by-path/snapshot", async (HttpContext ctx,
            IFolderRepository folderRepo,
            IArticleRepository articleRepo,
            ArticleService articleSvc,
            SessionService session,
            FolderAccessService folderAccess,
            string path) =>
        {
            var (userId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (userId is null) return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);
            if (!session.IsUnlocked) return Results.Json(new ErrorResponse("Owner session is locked"), statusCode: 423);
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new ErrorResponse("path is required"));

            // Per-path ACL state — needed for both the root check and the
            // recursive filter below.
            HashSet<string> deny = [], allow = [], readOnly = [];
            if (!isSuperadmin)
            {
                (deny, allow, readOnly) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(deny, allow, path))
                    return Results.Json(new ErrorResponse("Access denied"), statusCode: 403);
            }

            var root = await folderRepo.GetByPathAsync(path);
            if (root is null) return Results.NotFound(new ErrorResponse($"Folder {path} not found"));

            var allFolders = await folderRepo.GetAllActiveAsync();
            var prefix = root.Path.TrimEnd('/') + "/";
            // SECURITY: filter sub-folders by caller ACL — without this we leak
            // descendants the caller doesn't have read access to (e.g. allow on
            // /Public, deny on /Public/Secrets — without filtering, /Public/Secrets
            // would still go on the wire). Gemini security review 2026-05-25.
            var subtreeFolders = allFolders
                .Where(f => f.Id == root.Id || f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(f => isSuperadmin || !FolderAccessService.IsAccessDenied(deny, allow, f.Path))
                .Select(f => new
                {
                    id = f.Id, path = f.Path, name = f.Name, parentPath = f.ParentPath,
                    lamportTs = f.LamportTs, createdAt = f.CreatedAt, updatedAt = f.UpdatedAt
                })
                .ToList();

            // ListAsync(path) filters at SQL layer via LIKE prefix — much
            // cheaper than loading the whole vault into memory and filtering in
            // LINQ (kilo+gemini DoS finding for the snapshot endpoint).
            var subtreeArticles = await articleRepo.ListAsync(path);
            subtreeArticles = subtreeArticles
                .Where(a => isSuperadmin || !FolderAccessService.IsAccessDenied(deny, allow, a.TreePath))
                .ToList();
            // DoS cap — snapshot endpoint is a security-sensitive entry point.
            // Pagination is on the TODO list; for now, refuse oversized snapshots
            // and ask the caller to subscribe to a deeper subtree.
            const int MaxArticlesPerSnapshot = 500;
            if (subtreeArticles.Count > MaxArticlesPerSnapshot)
                return Results.Json(new ErrorResponse(
                    $"Folder contains {subtreeArticles.Count} articles, exceeds limit of {MaxArticlesPerSnapshot}. " +
                    "Subscribe to a more specific subfolder."), statusCode: 413);

            var articlePayload = new List<object>();
            long maxLamport = root.LamportTs;
            foreach (var a in subtreeArticles)
            {
                string? content = null;
                try { content = await articleSvc.GetContentAsync(a.Id); }
                catch { /* skip articles we can't decrypt */ }
                articlePayload.Add(new
                {
                    id = a.Id, title = a.Title, treePath = a.TreePath,
                    content, lamportTs = a.LamportTs,
                    createdAt = a.CreatedAt, updatedAt = a.UpdatedAt,
                    updatedBy = a.RemoteUpdatedBy
                });
                if (a.LamportTs > maxLamport) maxLamport = a.LamportTs;
            }

            return Results.Ok(new
            {
                rootPath = root.Path,
                folders = subtreeFolders,
                articles = articlePayload,
                cursor = maxLamport
            });
        }).WithTags("RemoteAuth");

        // Light freshness probe — returns (lamportTs, updatedAt) without body.
        // Used by friend-side UI for the "newer version available" toast.
        app.MapGet("/api/articles/{id:guid}/version", async (Guid id, HttpContext ctx,
            IArticleRepository articleRepo,
            FolderAccessService folderAccess) =>
        {
            var (userId, _, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (userId is null) return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);

            var article = await articleRepo.GetByIdUnfilteredAsync(id);
            if (article is null) return Results.NotFound(new ErrorResponse("Article not found"));

            if (!isSuperadmin)
            {
                var (deny, allow, _) = await folderAccess.GetFullAccessInfoAsync(userId);
                if (FolderAccessService.IsAccessDenied(deny, allow, article.TreePath))
                    return Results.NotFound(new ErrorResponse("Article not found"));
            }
            return Results.Ok(new { id = article.Id, version = article.LamportTs, updatedAt = article.UpdatedAt });
        }).WithTags("RemoteAuth");
    }
}

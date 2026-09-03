using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

public sealed partial class ChatToolDispatcher
{
    // ── READ tools ──────────────────────────────────────────────────────────

    // Mirrors BeeSearchTools.Search / SearchEndpoints GET /api/search (metadata path).
    private async Task<string> SearchAsync(JsonElement args)
    {
        var keywords = args.TryGetProperty("keywords", out var kw) ? kw.GetString() : null;
        if (string.IsNullOrWhiteSpace(keywords))
            return ErrorJson("keywords is required");

        var results = await searchService.SearchAsync(keywords!);
        return JsonSerializer.Serialize(new
        {
            folders = results.Folders.Select(f => new { path = f.Path, name = f.Name }),
            articles = results.Articles.Select(a => new { id = a.Id, title = a.Title, treePath = a.TreePath })
        }, JsonOpts);
    }

    // Mirrors BeeReadTools.ListArticles / ArticleEndpoints GET /api/articles.
    private async Task<string> ListArticlesAsync(JsonElement args)
    {
        var treePath = args.TryGetProperty("treePath", out var tp) ? tp.GetString() : null;
        var articles = await articleService.ListAsync(treePath);
        return JsonSerializer.Serialize(articles.Select(a => new
        {
            id = a.Id, title = a.Title, treePath = a.TreePath,
            status = a.Status, createdAt = a.CreatedAt, updatedAt = a.UpdatedAt
        }), JsonOpts);
    }

    // Mirrors BeeReadTools.GetTree (articleService.ListAsync + folderRepo.GetAllActiveAsync, both
    // scope-filtered by the ambient CallerScope).
    private async Task<string> GetTreeAsync(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;

        var articles = await articleService.ListAsync(path);
        var folders = await folderRepo.GetAllActiveAsync();

        var articlesByPath = articles
            .GroupBy(a => a.TreePath)
            .ToDictionary(g => g.Key, g => g.Select(a => new { id = a.Id, title = a.Title }).ToList());

        var folderMeta = folders.ToDictionary(f => f.Path, f => f);
        var allPaths = new HashSet<string>(folders.Select(f => f.Path));
        foreach (var a in articles) allPaths.Add(a.TreePath);

        var filteredPaths = path != null
            ? allPaths.Where(p => p == path || p.StartsWith(path.TrimEnd('/') + "/"))
            : allPaths;

        var byPath = filteredPaths.OrderBy(p => p).Select(p =>
        {
            folderMeta.TryGetValue(p, out var meta);
            return new
            {
                path = p,
                isSystem = meta?.IsSystem ?? false,
                isRemote = meta?.RemoteSubscriptionId.HasValue ?? false,
                articles = articlesByPath.TryGetValue(p, out var arts) ? arts.Cast<object>().ToList() : new List<object>()
            };
        });

        return JsonSerializer.Serialize(new { paths = byPath }, JsonOpts);
    }

    // Shares BeeReadTools.GetArticle's gate order via ArticleContentPolicy (plan §1 CRITICAL):
    // metadata (scope-filtered) -> protected check -> session-lock check -> explicit folder-ACL
    // re-check -> decrypt. Content is withheld — never an exception — for every reason a caller
    // might not get it, so a locked vault or a denied folder degrades to a clear tool result.
    private async Task<string> GetArticleAsync(JsonElement args, HttpContext ctx)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");

        // Default true; only an explicit JSON false disables the content read.
        var includeContent = true;
        if (args.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.False)
            includeContent = false;

        var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
        var gate = await ArticleContentPolicy.ResolveAsync(
            id, includeContent, userId, agentId, isSuperadmin, articleService, session, folderAccess);

        if (gate.Status == ArticleContentPolicy.Status.NotFound)
            return ErrorJson($"article {id} not found");

        var article = gate.Article!;
        var tags = await conceptTagService.GetByArticleIdAsync(id);
        var related = await conceptTagService.GetRelatedArticlesAsync(id);
        var relatedCount = related.Count;
        var relatedStrength = related.Sum(r => r.Strength);

        return gate.Status switch
        {
            // Protected articles: second-layer (passphrase) encryption. No passphrase in the chat
            // path (and none ever will be) — body stays opaque.
            ArticleContentPolicy.Status.Protected => JsonSerializer.Serialize(new
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount, relatedStrength,
                content = (string?)null,
                isProtected = true,
                notice = "This article is password-protected (second-layer encryption). Its body can only be unlocked by a human in the web/mobile UI.",
                createdAt = article.CreatedAt, updatedAt = article.UpdatedAt
            }, JsonOpts),

            ArticleContentPolicy.Status.Locked => JsonSerializer.Serialize(new
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount, relatedStrength,
                content = (string?)null,
                isLocked = true,
                notice = "The vault is locked. Unlock it to read article content; metadata is still available."
            }, JsonOpts),

            ArticleContentPolicy.Status.AccessDenied => JsonSerializer.Serialize(new
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount, relatedStrength,
                content = (string?)null,
                accessDenied = true,
                notice = "You don't have permission to read this article's content."
            }, JsonOpts),

            ArticleContentPolicy.Status.Ok when includeContent => JsonSerializer.Serialize(new
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount, relatedStrength,
                content = gate.Content,
                createdAt = article.CreatedAt, updatedAt = article.UpdatedAt
            }, JsonOpts),

            _ => JsonSerializer.Serialize(new // Ok, metadata only (content=false)
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount, relatedStrength,
                createdAt = article.CreatedAt, updatedAt = article.UpdatedAt
            }, JsonOpts)
        };
    }

    // Mirrors BeeSearchTools.SearchContent / POST /api/search/hybrid.
    // Degrades to title-only search (with a notice) when locked or when ranked/semantic search is
    // unavailable, rather than failing outright; an unrecognized mode value is a hard error.
    private async Task<string> SearchContentAsync(JsonElement args)
    {
        var keywords = args.TryGetProperty("keywords", out var kw) ? kw.GetString() : null;
        if (string.IsNullOrWhiteSpace(keywords))
            return ErrorJson("keywords is required");

        var modeArg = args.TryGetProperty("mode", out var m) ? m.GetString() : null;
        var parsedMode = SearchMode.Hybrid;
        if (!string.IsNullOrEmpty(modeArg) && !Enum.TryParse(modeArg, ignoreCase: true, out parsedMode))
            return ErrorJson($"invalid mode '{modeArg}'. Valid values: hybrid, keyword, semantic.");

        var titleMatches = await searchService.SearchAsync(keywords!);

        string? notice = null;
        List<Article> ranked;
        if (!session.IsUnlocked)
        {
            ranked = [];
            notice = "Vault is locked — searched titles/metadata only (body search skipped).";
        }
        else
        {
            try
            {
                ranked = await hybridSearchService.SearchAsync(keywords!, parsedMode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ModelUnavailableException)
            {
                ranked = [];
                notice = parsedMode == SearchMode.Semantic
                    ? "Meaning-based search is unavailable on this node — searched titles/metadata only."
                    : "Ranked content search is unavailable — searched titles/metadata only.";
            }
        }

        var seenIds = new HashSet<Guid>(ranked.Select(a => a.Id));
        var merged = new List<Article>(ranked);
        foreach (var a in titleMatches.Articles)
        {
            if (seenIds.Add(a.Id)) merged.Add(a);
        }

        return JsonSerializer.Serialize(new
        {
            folders = titleMatches.Folders.Select(f => new { path = f.Path, name = f.Name }),
            articles = merged.Select(a => new { id = a.Id, title = a.Title, treePath = a.TreePath }),
            notice
        }, JsonOpts);
    }
}

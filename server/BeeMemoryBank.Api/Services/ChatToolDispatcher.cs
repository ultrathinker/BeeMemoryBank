using System.Diagnostics;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Curated, deny-by-default READ-ONLY tool surface for the native AI chat (plan §1, §2 Phase 1).
///
/// <para>Each tool calls the SAME scope-checked Core service methods the REST endpoints and the
/// MCP read tools use (<see cref="SearchService"/>, <see cref="ArticleService"/>,
/// <see cref="ConceptTagService"/>, <see cref="FolderAccessService"/>), running under the
/// request's ambient <c>CallerScope</c> (set by <c>CallerScopeMiddleware</c>). The repos filter
/// reads automatically by that scope, so the AI can only ever see what the calling user can see —
/// this is also the prompt-injection backstop.</para>
///
/// <para><b>CRITICAL ACL gate (plan §1):</b> <c>bee_get_article</c>'s content branch mirrors the
/// <c>ArticleEndpoints</c> <c>/{id}/content</c> handler EXACTLY — <see cref="ArticleService.GetMetadataAsync"/>
/// (scope-filtered, null if denied) → <see cref="FolderAccessService.IsAccessDenied"/> on the
/// metadata's TreePath → only then <see cref="ArticleService.GetContentAsync"/>. Calling
/// <c>GetContentAsync</c> directly bypasses folder ACLs (it hits the body repo with no scope
/// filter) and MUST NOT be done.</para>
///
/// <para>Every content-touching branch re-checks <see cref="SessionService.IsUnlocked"/> and returns
/// a clear "vault is locked" tool RESULT (never an exception) when locked — so a locked vault
/// degrades gracefully instead of crashing the tool loop.</para>
///
/// <para><b>Phase 3 — guarded writes.</b> The write tools (<c>bee_save_article</c>,
/// <c>bee_update_article</c>, <c>bee_append_to_article</c>, <c>bee_replace_in_article</c>,
/// <c>bee_delete_article</c>) call the SAME scope-checked <see cref="ArticleService"/> methods the
/// REST endpoints and MCP write tools use (never raw repos/SQL), and catch
/// <see cref="ReadOnlyAccessException"/>/<see cref="UnauthorizedAccessException"/> as graceful tool
/// results — exactly mirroring <c>BeeWriteTools</c>. They are NEVER executed inline by the tool
/// loop: the streaming loop pauses on any write tool call behind a human-in-the-loop confirm SSE
/// gate (<c>confirm_required</c>) and only the dedicated confirm endpoint runs them after the user
/// clicks Allow (see <c>ChatEndpoints</c>). Tag rename/merge/delete, folder delete, hard-delete, DEK
/// rotation, snapshot, user/agent admin, and audit tools are deliberately NOT exposed (plan §1).</para>
/// </summary>
public sealed class ChatToolDispatcher(
    ArticleService articleService,
    SearchService searchService,
    IFolderRepository folderRepo,
    ConceptTagService conceptTagService,
    FolderAccessService folderAccess,
    SessionService session,
    ChatAttachmentRepository attachRepo,
    MediaService mediaService)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The tool definitions declared to the model (deny-by-default: only these are exposed).</summary>
    public static IReadOnlyList<Models.ChatToolDefinition> ToolDefinitions { get; } = BuildToolDefinitions();

    // ── Phase 3: write-tool classification + audit-tagging plumbing ──────────

    /// <summary>Write tools (plan §1 "Tool surface"). Read tools execute immediately inside the
    /// tool loop; write tools are PAUSED behind a human-in-the-loop confirm SSE gate and only ever
    /// executed by the confirm endpoint after the user clicks Allow. Kept as a public set so the
    /// streaming loop can decide "emit confirm_required + pause" vs "execute now".</summary>
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>
    {
        "bee_save_article", "bee_update_article", "bee_append_to_article",
        "bee_replace_in_article", "bee_delete_article", "bee_insert_image_into_article"
    };

    /// <summary>Destructive tools (plan §2 Phase 3 "per-session destructive-op cap"). These are
    /// counted per conversation and refused beyond a small cap (see ChatDestructiveOpCounter).</summary>
    public static readonly IReadOnlySet<string> DestructiveTools = new HashSet<string>
    {
        "bee_delete_article", "bee_replace_in_article"
    };

    public static bool IsWriteTool(string name) => WriteTools.Contains(name);
    public static bool IsDestructiveTool(string name) => DestructiveTools.Contains(name);

    /// <summary>The value recorded as <c>ViaAgentName</c> on chat-driven writes so /Activity shows
    /// "via agent: chat" exactly like MCP-agent-driven edits (plan §1 "Audit", §2 Phase 3).</summary>
    public const string ChatViaAgentName = "chat";

    /// <summary>The <see cref="HttpContext.Items"/> key set while a chat write tool executes, so
    /// <c>HttpActorProvider.ViaAgentName</c> can read it. Read tools are deliberately NOT tagged.</summary>
    public const string ChatActorItemsKey = "ChatViaAgentName";

    /// <summary>The <see cref="HttpContext.Items"/> key the confirm endpoint sets BEFORE executing a
    /// user-approved write. <see cref="InvokeAsync"/> refuses to run ANY write tool unless this is
    /// present — so the Phase 1 non-streaming <c>/message</c> loop (which calls InvokeAsync for every
    /// tool but has no confirm gate) can NEVER execute an ungated write. Only <c>/confirm</c> (after a
    /// human Allow) sets it. Defense-in-depth on top of the /stream loop never calling InvokeAsync for
    /// writes in the first place.</summary>
    public const string ChatWriteExecItemsKey = "ChatWriteExec";

    /// <summary>Short human-readable summary of a write tool call, shown on the confirm card
    /// ("AI wants to: &lt;summary&gt;"). Built from the tool name + the model's args; never throws.</summary>
    public static string SummarizeWriteCall(string name, JsonElement args)
    {
        string Id() => args.TryGetProperty("id", out var e) ? (e.GetString() ?? "") : "";
        string Title() => args.TryGetProperty("title", out var e) ? (e.GetString() ?? "") : "";
        string Path() => args.TryGetProperty("treePath", out var e) ? (e.GetString() ?? "") : "";
        return name switch
        {
            "bee_save_article" => string.IsNullOrWhiteSpace(Title())
                ? $"Create a new article{(string.IsNullOrWhiteSpace(Path()) ? "" : " in " + Path())}"
                : $"Create article '{Title()}'{(string.IsNullOrWhiteSpace(Path()) ? "" : " in " + Path())}",
            "bee_update_article" => $"Update article {Id()}{(string.IsNullOrWhiteSpace(Title()) ? "" : " → '" + Title() + "'")}",
            "bee_append_to_article" => $"Append text to article {Id()}",
            "bee_replace_in_article" => $"Replace text in article {Id()}",
            "bee_delete_article" => $"Delete article {Id()}",
            "bee_insert_image_into_article" => args.TryGetProperty("articleId", out var aid)
                ? $"Insert an image into article {aid.GetString()}"
                : $"Create article '{Title()}'{(string.IsNullOrWhiteSpace(Path()) ? "" : " in " + Path())} with an inserted image",
            _ => $"Run {name}"
        };
    }

    // ── public surface ──────────────────────────────────────────────────────

    /// <summary>Dispatches a single tool call. Returns a JSON-serialized tool result. NEVER throws
    /// on expected failures (locked / not-found / denied / bad args) — those become structured
    /// tool results so the model can recover. Only truly unexpected errors propagate.</summary>
    ///
    /// <para><b>Phase 3 audit tagging:</b> while a write tool executes, the ambient
    /// <see cref="HttpContext.Items"/> is tagged (<see cref="ChatActorItemsKey"/>=<see cref="ChatViaAgentName"/>)
    /// so <c>HttpActorProvider.ViaAgentName</c> reports "chat" and the resulting audit-log event is
    /// attributed to the AI (read tools are NOT tagged — only writes). Writes also re-check
    /// <see cref="SessionService.IsUnlocked"/> (encrypt/decrypt needs the master DEK) and degrade to a
    /// graceful "vault is locked" tool result rather than throwing.</para>
    public async Task<ToolDispatchResult> InvokeAsync(string name, JsonElement args, HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var isWrite = IsWriteTool(name);

        // Tag the context only for the duration of a write tool call. Restored on exit so a later
        // read tool in the same request is never mis-attributed.
        var hadMarker = ctx.Items.ContainsKey(ChatActorItemsKey);
        if (isWrite)
            ctx.Items[ChatActorItemsKey] = ChatViaAgentName;

        try
        {
            // Writes need the master DEK (bodies are encrypted/decrypted through ArticleService).
            // Mirror the read path: degrade to a clear tool result, never an exception.
            if (isWrite && !session.IsUnlocked)
            {
                sw.Stop();
                return new ToolDispatchResult(
                    ErrorJson("The vault is locked. Unlock it to make changes."),
                    Ok: true, DurationMs: (int)sw.ElapsedMilliseconds, Error: null);
            }

            // Phase 3 confirm-gate: a write may ONLY execute when the confirm endpoint has set the
            // ChatWriteExec marker (i.e. after a human Allow). The streaming loop never reaches here
            // for writes (it pauses on confirm_required); this guard prevents any OTHER caller of
            // InvokeAsync — notably the non-streaming /message loop, which has no confirm gate — from
            // executing an ungated write. Graceful tool result, never an exception.
            var execAllowed = ctx.Items.TryGetValue(ChatWriteExecItemsKey, out var execObj)
                              && execObj is bool eb && eb;
            if (isWrite && !execAllowed)
            {
                sw.Stop();
                return new ToolDispatchResult(
                    ErrorJson("This action requires explicit user approval and can only run through the chat confirm flow."),
                    Ok: true, DurationMs: (int)sw.ElapsedMilliseconds, Error: null);
            }

            string json = name switch
            {
                "bee_search" => await SearchAsync(args),
                "bee_list_articles" => await ListArticlesAsync(args),
                "bee_get_tree" => await GetTreeAsync(args),
                "bee_get_article" => await GetArticleAsync(args, ctx),
                "bee_search_content" => await SearchContentAsync(args),
                "bee_save_article" => await SaveArticleAsync(args),
                "bee_update_article" => await UpdateArticleAsync(args),
                "bee_append_to_article" => await AppendToArticleAsync(args),
                "bee_replace_in_article" => await ReplaceInArticleAsync(args),
                "bee_delete_article" => await DeleteArticleAsync(args),
                "bee_insert_image_into_article" => await InsertImageIntoArticleAsync(args, ctx),
                // generate_image is handled directly in ChatEndpoints.RunToolLoopAsync (it needs
                // OpenRouter egress + attachment storage + SSE). If InvokeAsync is reached for it,
                // return a clear error rather than a confusing "unknown tool".
                "generate_image" => ErrorJson("generate_image is handled by the chat loop, not the dispatcher."),
                _ => ErrorJson($"Unknown tool '{name}'. Available tools: search, list_articles, get_tree, get_article, search_content, save_article, update_article, append_to_article, replace_in_article, delete_article, insert_image_into_article, generate_image.")
            };
            sw.Stop();
            return new ToolDispatchResult(json, Ok: true, DurationMs: (int)sw.ElapsedMilliseconds, Error: null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ToolDispatchResult(
                ErrorJson($"Tool '{name}' failed: {ex.Message}"),
                Ok: false,
                DurationMs: (int)sw.ElapsedMilliseconds,
                Error: ex.Message);
        }
        finally
        {
            // Only ever remove a marker WE set — never clobber one placed by an outer caller.
            if (isWrite && !hadMarker)
                ctx.Items.Remove(ChatActorItemsKey);
        }
    }

    public record ToolDispatchResult(string Json, bool Ok, int DurationMs, string? Error);

    // ── tools ───────────────────────────────────────────────────────────────

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

    // Mirrors BeeReadTools.GetArticle, BUT adds the ArticleEndpoints /{id}/content ACL gate
    // (plan §1 CRITICAL) before any content read, and returns "vault is locked" as a tool result
    // (not an exception) when the session is locked.
    private async Task<string> GetArticleAsync(JsonElement args, HttpContext ctx)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");

        // Default true; only an explicit JSON false disables the content read.
        var includeContent = true;
        if (args.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.False)
            includeContent = false;

        // GetMetadataAsync is scope-filtered (ArticleRepository.GetByIdAsync returns null when the
        // caller's scope denies the article's tree path) → this is the first ACL gate.
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return ErrorJson($"article {id} not found");

        var tags = await conceptTagService.GetByArticleIdAsync(id);
        var related = await conceptTagService.GetRelatedArticlesAsync(id);

        // Protected articles: second-layer (passphrase) encryption. No passphrase in the chat path
        // (and none ever will be) → body stays opaque. Mirror BeeReadTools.
        if (includeContent && article.Protected)
        {
            return JsonSerializer.Serialize(new
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount = related.Count, relatedStrength = related.Sum(r => r.Strength),
                content = (string?)null,
                isProtected = true,
                notice = "This article is password-protected (second-layer encryption). Its body can only be unlocked by a human in the web/mobile UI.",
                createdAt = article.CreatedAt, updatedAt = article.UpdatedAt
            }, JsonOpts);
        }

        if (includeContent)
        {
            // Defense-in-depth: a content read requires an unlocked session even though the
            // /message endpoint already gates on it. Return a clear tool result, not an exception.
            if (!session.IsUnlocked)
            {
                return JsonSerializer.Serialize(new
                {
                    id = article.Id, title = article.Title, treePath = article.TreePath,
                    tags, relatedCount = related.Count, relatedStrength = related.Sum(r => r.Strength),
                    content = (string?)null,
                    isLocked = true,
                    notice = "The vault is locked. Unlock it to read article content; metadata is still available."
                }, JsonOpts);
            }

            // Folder ACL gate — mirrors ArticleEndpoints /{id}/content. GetContentAsync goes straight
            // to the body repo with NO scope filter, so without this check any caller who happens to
            // know an article GUID could read its plaintext.
            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            if (!isSuperadmin)
            {
                var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
                if (FolderAccessService.IsAccessDenied(denyPaths, allowPaths, article.TreePath))
                {
                    return JsonSerializer.Serialize(new
                    {
                        id = article.Id, title = article.Title, treePath = article.TreePath,
                        tags, relatedCount = related.Count, relatedStrength = related.Sum(r => r.Strength),
                        content = (string?)null,
                        accessDenied = true,
                        notice = "You don't have permission to read this article's content."
                    }, JsonOpts);
                }
            }

            string plaintext;
            try
            {
                plaintext = await articleService.GetContentAsync(id);
            }
            catch (InvalidOperationException ex)
            {
                return ErrorJson(ex.Message);
            }

            return JsonSerializer.Serialize(new
            {
                id = article.Id, title = article.Title, treePath = article.TreePath,
                tags, relatedCount = related.Count, relatedStrength = related.Sum(r => r.Strength),
                content = plaintext,
                createdAt = article.CreatedAt, updatedAt = article.UpdatedAt
            }, JsonOpts);
        }

        return JsonSerializer.Serialize(new
        {
            id = article.Id, title = article.Title, treePath = article.TreePath,
            tags, relatedCount = related.Count, relatedStrength = related.Sum(r => r.Strength),
            createdAt = article.CreatedAt, updatedAt = article.UpdatedAt
        }, JsonOpts);
    }

    // Mirrors BeeSearchTools.SearchContent / SearchEndpoints GET /api/search?content=true.
    // Silently degrades to title-only search when locked (SearchService behavior).
    private async Task<string> SearchContentAsync(JsonElement args)
    {
        var keywords = args.TryGetProperty("keywords", out var kw) ? kw.GetString() : null;
        if (string.IsNullOrWhiteSpace(keywords))
            return ErrorJson("keywords is required");

        var results = await searchService.SearchWithContentAsync(keywords!);
        return JsonSerializer.Serialize(new
        {
            folders = results.Folders.Select(f => new { path = f.Path, name = f.Name }),
            articles = results.Articles.Select(a => new { id = a.Id, title = a.Title, treePath = a.TreePath }),
            notice = session.IsUnlocked ? null : "Vault is locked — searched titles/metadata only (body search skipped)."
        }, JsonOpts);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string ErrorJson(string message) => JsonSerializer.Serialize(new { error = message }, JsonOpts);

    // Reads a "tags" array argument into a list. Empty array → empty list. Never null.
    private static List<string> ReadTags(JsonElement el)
    {
        var list = new List<string>();
        if (el.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in el.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static string OkJson(string message, Guid? id = null)
    {
        var o = new Dictionary<string, object?> { ["ok"] = true, ["message"] = message };
        if (id.HasValue) o["id"] = id.Value;
        return JsonSerializer.Serialize(o, JsonOpts);
    }

    // ── WRITE tools (Phase 3 — confirm-gated; never run inline by the tool loop) ──
    //
    // Each mirrors the corresponding BeeWriteTools method: same scope-checked ArticleService calls,
    // same ACL-error handling (ReadOnlyAccessException → "read-only folder", UnauthorizedAccessException
    // → "restricted folder"), same two-step confirm for delete. ACL is enforced BY REUSE — the Core
    // repos throw these on write; we surface them as graceful tool results, never exceptions. A locked
    // vault is already rejected up-front in InvokeAsync (writes need the master DEK).

    // Mirrors BeeWriteTools.SaveArticle / ArticleEndpoints POST /api/articles.
    private async Task<string> SaveArticleAsync(JsonElement args)
    {
        var title = args.TryGetProperty("title", out var t) ? t.GetString() : null;
        var treePath = args.TryGetProperty("treePath", out var tp) ? tp.GetString() : null;
        var content = args.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return ErrorJson("title is required");
        if (string.IsNullOrWhiteSpace(treePath)) return ErrorJson("treePath is required");
        if (content == null) return ErrorJson("content is required");
        var tags = args.TryGetProperty("tags", out var tagsEl) ? ReadTags(tagsEl) : [];

        try
        {
            // CreateAsync encrypts the body under a per-article DEK (wrapped under the master DEK),
            // creates folders as needed, and logs the create event (synced like a human edit).
            var article = await articleService.CreateAsync(title!, treePath!, tags, content);
            return OkJson($"Created article '{article.Title}' in {article.TreePath}.", article.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: target folder is restricted.");
        }
        catch (ArgumentException ex) { return ErrorJson(ex.Message); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.UpdateArticle / ArticleEndpoints PUT /api/articles/{id}. Only provided
    // fields are touched (ArticleService.UpdateAsync keeps omitted fields). Content is optional.
    private async Task<string> UpdateArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");

        string? title = args.TryGetProperty("title", out var t2) ? t2.GetString() : null;
        string? treePath = args.TryGetProperty("treePath", out var tp2) ? tp2.GetString() : null;
        string? content = args.TryGetProperty("content", out var c2) ? c2.GetString() : null;
        // tags: absent → null (keep current); present (even []) → replace (matches BeeWriteTools).
        List<string>? tags = args.TryGetProperty("tags", out var tagsEl) ? ReadTags(tagsEl) : null;
        var hasContent = content != null;

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");
        // Protected (second-layer) bodies are opaque to agents — a human must edit them in the UI.
        if (article.Protected && hasContent)
            return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body. A human must edit it in the web/mobile UI.");

        try
        {
            await articleService.UpdateAsync(id, title, treePath, tags, content);
            return OkJson($"Updated article {id} ({article.Title}).", id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
        catch (ArgumentException ex) { return ErrorJson(ex.Message); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.AppendToArticle.
    private async Task<string> AppendToArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");
        var text = args.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        if (string.IsNullOrEmpty(text)) return ErrorJson("text is required");

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");
        if (article.Protected)
            return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body.");

        try
        {
            var content = await articleService.GetContentAsync(id);
            var newContent = content + "\n\n" + text;
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return OkJson($"Appended to article {id} ({article.Title}). New size: {newContent.Length} chars.", id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.ReplaceInArticle (case-sensitive substring replace; N=0 is a no-op).
    private async Task<string> ReplaceInArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");
        var search = args.TryGetProperty("search", out var se) ? se.GetString() : null;
        var replace = args.TryGetProperty("replace", out var re) ? re.GetString() : null;
        if (string.IsNullOrEmpty(search)) return ErrorJson("search is required");
        if (replace == null) return ErrorJson("replace is required");
        if (search == replace) return ErrorJson("search and replace are identical");

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");
        if (article.Protected)
            return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body.");

        try
        {
            var content = await articleService.GetContentAsync(id);
            var count = CountOccurrences(content, search);
            if (count == 0)
                return JsonSerializer.Serialize(new { ok = true, occurrences = 0, message = $"Replaced 0 occurrences of \"{Truncate(search, 50)}\" in article {id} ({article.Title}).", id }, JsonOpts);
            var newContent = content.Replace(search, replace);
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return JsonSerializer.Serialize(new { ok = true, occurrences = count, message = $"Replaced {count} occurrence(s) of \"{Truncate(search, 50)}\" → \"{Truncate(replace, 50)}\" in article {id} ({article.Title}).", id }, JsonOpts);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }
    }

    // Mirrors BeeWriteTools.DeleteArticle. Keeps the two-step `confirm` flag semantics: the method
    // itself warns when confirm is false and only soft-deletes when true. In the chat flow the
    // confirm endpoint forces confirm=true at execution (the human's Allow click IS the confirmation),
    // so the two-step check stays intact as defense-in-depth without double-prompting the user.
    private async Task<string> DeleteArticleAsync(JsonElement args)
    {
        if (!args.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            return ErrorJson("id (GUID) is required");
        var confirm = args.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True;
        if (!confirm)
            return ErrorJson($"Warning: this will soft-delete article {id}. Set confirm=true to proceed. (In chat the user is asked to approve first.)");

        var article = await articleService.GetMetadataAsync(id);
        if (article == null) return ErrorJson($"article {id} not found");

        try
        {
            await articleService.DeleteAsync(id);
            return OkJson($"Deleted article {id} ({article.Title}). It is hidden from search/lists and can be restored from the web UI.", id);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: article is in a restricted folder.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {id} not found"); }
    }

    // Inserts a chat attachment image into an article: uploads the blob into the article media store
    // (MediaService — same path as POST /api/media) and appends/creates the markdown reference.
    // Attachment ownership is enforced by the user-scoped repo lookup; article/media write ACL is
    // enforced at the repository layer (MediaRepository.EnsureWriteAllowedAsync + ArticleRepository
    // scope checks throw UnauthorizedAccessException / ReadOnlyAccessException), which is caught here
    // and surfaced as a graceful tool result.
    private async Task<string> InsertImageIntoArticleAsync(JsonElement args, HttpContext ctx)
    {
        if (!args.TryGetProperty("attachmentId", out var attEl) || !Guid.TryParse(attEl.GetString(), out var attachmentId))
            return ErrorJson("attachmentId (GUID) is required");

        Guid? articleId = args.TryGetProperty("articleId", out var aEl) && Guid.TryParse(aEl.GetString(), out var aid) ? aid : null;
        var title = args.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
        var treePath = args.TryGetProperty("treePath", out var tpEl) ? tpEl.GetString() : null;
        var caption = args.TryGetProperty("caption", out var cEl) ? cEl.GetString() : null;

        var newArticle = articleId is null;
        if (newArticle && (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(treePath)))
            return ErrorJson("Provide either articleId (existing article) or title + treePath (new article).");
        if (!newArticle && (title != null || treePath != null))
            return ErrorJson("Provide articleId OR title+treePath, not both.");

        // Ownership: the attachment must belong to a conversation of the calling user.
        var (userId, _, _) = CallerIdentity.Extract(ctx);
        if (userId is null) return ErrorJson("Unauthorized");
        var attachment = await attachRepo.GetByIdForUserAsync(attachmentId, userId.Value);
        if (attachment?.Blob is not { Length: > 0 })
            return ErrorJson($"attachment {attachmentId} not found (use an attachmentId from the attachment manifest or a generate_image result, copied exactly)");

        try
        {
            var fileName = "chat-image-" + attachmentId.ToString("N") + ExtensionForMime(attachment.Mime);
            var alt = string.IsNullOrWhiteSpace(caption) ? "image" : caption!.Trim();

            if (newArticle)
            {
                // New-article orphan-avoidance: upload the image as an ORPHAN media row FIRST
                // (articleId: null — MediaRepository skips the article ACL/scope check when there is
                // no article yet), THEN create the article with the image markdown already embedded in
                // its initial content. ArticleService.CreateAsync calls LinkOrphanMediaAsync, which
                // binds the orphan media referenced in the body to the newly created article. On any
                // failure between the two steps only an invisible orphan media blob remains — never a
                // visible empty article.
                var media = await mediaService.CreateAsync(fileName, attachment.Mime, attachment.Blob!, null);
                var figureMd = $"![{alt}](/api/media/{media.Id})";
                var created = await articleService.CreateAsync(title!, treePath!, [], figureMd);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = $"Created article '{created.Title}' in {created.TreePath} with the inserted image ({figureMd}).",
                    id = created.Id,          // "id" so TryExtractArticleId gives the UI an open-article link
                    mediaId = media.Id,
                    mediaUrl = $"/api/media/{media.Id}"
                }, JsonOpts);
            }

            // Existing-article branch: resolve the target, reject protected bodies, then upload media
            // bound to the resolved article and append the figure markdown.
            var article = await articleService.GetMetadataAsync(articleId!.Value);
            if (article == null) return ErrorJson($"article {articleId} not found");
            if (article.Protected)
                return ErrorJson("This article is password-protected (second-layer encryption); the AI cannot change its body.");

            var existingMedia = await mediaService.CreateAsync(fileName, attachment.Mime, attachment.Blob!, article.Id);
            var existingFigureMd = $"![{alt}](/api/media/{existingMedia.Id})";
            try
            {
                var existing = await articleService.GetContentAsync(article.Id);
                var newContent = string.IsNullOrEmpty(existing) ? existingFigureMd : existing + "\n\n" + existingFigureMd;
                await articleService.UpdateAsync(article.Id, null, null, null, newContent);
            }
            catch
            {
                // Compensate: don't leave an orphan media blob if the body update failed.
                try { await mediaService.DeleteAsync(existingMedia.Id); } catch { /* best effort */ }
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                message = $"Inserted image into article {article.Id} ({article.Title}) as {existingFigureMd}.",
                id = article.Id,          // "id" so TryExtractArticleId gives the UI an open-article link
                mediaId = existingMedia.Id,
                mediaUrl = $"/api/media/{existingMedia.Id}"
            }, JsonOpts);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? ErrorJson($"Access denied: folder '{path}' is read-only for your user.")
                : ErrorJson("Access denied: target folder is restricted.");
        }
        catch (KeyNotFoundException) { return ErrorJson($"article {articleId} not found"); }
        catch (ArgumentException ex) { return ErrorJson(ex.Message); }        // MediaService size/type limits
        catch (InvalidOperationException ex) { return ErrorJson(ex.Message); }

        static string ExtensionForMime(string mime) => mime.ToLowerInvariant() switch
        {
            "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp",
            "image/gif" => ".gif", "image/svg+xml" => ".svg", _ => ".img"
        };
    }

    private static List<Models.ChatToolDefinition> BuildToolDefinitions()
    {
        return
        [
            Tool("bee_search", "Search both articles (by title) AND folders (by name/path), case-insensitive. Fast metadata search. Returns { folders, articles }. Use this first; use bee_search_content only for body-text matches.",
                P("keywords", "string", "Search keywords (article titles + folder names/paths).", required: true)),
            Tool("bee_list_articles", "List articles, optionally filtered by tree path. Returns [{ id, title, treePath, status, createdAt, updatedAt }]. Omit treePath to list everything.",
                P("treePath", "string", "Optional tree path filter, e.g. '/Work'. Omit to list all.")),
            Tool("bee_get_tree", "Get the folder/article tree including empty folders. Returns { paths: [{ path, isSystem, isRemote, articles: [{ id, title }] }] }. Optional 'path' scopes to one subtree.",
                P("path", "string", "Optional subtree filter, e.g. '/Work'. Omit for the whole tree.")),
            Tool("bee_get_article", "Get an article. Returns metadata (id, title, treePath, tags, relatedCount, relatedStrength, createdAt, updatedAt) and, when content=true (default), the decrypted body. Content is withheld when the vault is locked, the article is password-protected, or the caller lacks folder access — each reported as a structured field, not an error.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("content", "boolean", "Include decrypted body. Default true.")),
            Tool("bee_search_content", "Full-text search inside article BODIES (slow — decrypts/scans). Returns the same shape as bee_search. Silently degrades to title-only when the vault is locked.",
                P("keywords", "string", "Keywords to find in article body text.", required: true)),
            // ── Phase 3 write tools (confirm-gated: the user is shown an Allow/Deny card before any of these run) ──
            Tool("bee_save_article", "Create a new article (title, treePath, content, optional tags). Folders in treePath are auto-created. The user will be asked to APPROVE this before it runs.",
                P("title", "string", "Article title.", required: true),
                P("treePath", "string", "Tree path, e.g. '/Work/Dev'. Must start with '/'.", required: true),
                P("content", "string", "Article body in Markdown.", required: true),
                P("tags", "array", "Optional tags as a JSON array of strings, e.g. [\"a\",\"b\"].", itemsType: "string")),
            Tool("bee_update_article", "Update an existing article's title, treePath, content, and/or tags. Only provided fields change; omitted fields are untouched. Replaces content fully when provided. The user will be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("title", "string", "New title (omit to keep)."),
                P("treePath", "string", "New tree path (omit to keep)."),
                P("content", "string", "New full body in Markdown (omit to keep)."),
                P("tags", "array", "New tag list as a JSON array of strings. Replaces ALL tags. Omit to keep; pass [] to clear.", itemsType: "string")),
            Tool("bee_append_to_article", "Append text to the end of an article (a blank line is inserted first). Cheaper than bee_update_article for adding a section/note. The user will be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("text", "string", "Text to append.", required: true)),
            Tool("bee_replace_in_article", "Case-sensitive substring find-and-replace within one article (not regex). Returns the occurrence count; 0 means no change. The user will be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("search", "string", "Exact text to find (case-sensitive).", required: true),
                P("replace", "string", "Replacement text.", required: true)),
            Tool("bee_delete_article", "Soft-delete an article (hidden from search/lists; restorable from the web UI). You MUST set confirm=true. The user will ALSO be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("confirm", "boolean", "Must be true to delete. The user is separately asked to approve.")),
            // Image generation tool — declared alongside the bee_* tools so the text model can call
            // it from its normal tool loop. The actual egress (OpenRouter image-gen call + storing
            // the result as a chat attachment + emitting the inline image SSE event) is handled in
            // ChatEndpoints.RunToolLoopAsync, NOT here (the dispatcher has no OpenRouter/SSE access).
            // If no image-gen model is configured, the handler returns a graceful error tool result
            // so the model can tell the user in plain text.
            Tool("generate_image", "Generate an image from a text prompt. The generated image appears inline in the chat. Use this ONLY when the user explicitly asks to create, generate, or draw an image. If image generation is not configured, you will receive an error — tell the user in plain text.",
                P("prompt", "string", "A detailed description of the image to generate.", required: true)),
            // Inserts an attached/generated chat image into an article: uploads the blob to the article
            // media store and appends (or creates) the ![caption](/api/media/{id}) markdown reference.
            // The model must address the image by its attachmentId, surfaced via the attachment manifest
            // (after each image-bearing message) and the generate_image tool result (attachmentIds).
            Tool("bee_insert_image_into_article",
                "Insert a chat image (a user-uploaded attachment or a previously generated image) into an article's content. " +
                "Pass the attachmentId from the attachment manifest or from a generate_image result — copy the id EXACTLY, character for character, and never invent one. " +
                "Target EITHER an existing article (articleId — the image markdown is appended to its body) OR a new article " +
                "(title + treePath — the article is created with the image as its content). " +
                "The image is uploaded to the article media store and referenced as ![caption](/api/media/{id}) markdown. " +
                "The user will be asked to APPROVE this before it runs.",
                P("attachmentId", "string", "Chat attachment ID (GUID) of the image to insert. Copy it EXACTLY from the attachment manifest or a generate_image result.", required: true, format: "uuid"),
                P("articleId", "string", "Existing article ID (GUID). Omit when creating a new article.", format: "uuid"),
                P("title", "string", "Title for a NEW article. Required together with treePath when articleId is omitted."),
                P("treePath", "string", "Tree path for a NEW article, e.g. '/Work/Dev'. Must start with '/'."),
                P("caption", "string", "Optional image caption, used as the markdown alt text.")),
        ];
    }

    // Tiny JSON-Schema builder so we don't hand-write raw JSON strings.
    private static Models.ChatToolDefinition Tool(string name, string description, params (string pname, JsonElement schema, bool required)[] props)
    {
        var required = props.Where(p => p.required).Select(p => p.pname).ToArray();
        var properties = props.ToDictionary(p => p.pname, p => (object?)p.schema);
        var root = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Length > 0)
            root["required"] = required;
        return new Models.ChatToolDefinition
        {
            Function = new Models.ChatToolFunction { Name = name, Description = description, Parameters = ToElement(root) }
        };
    }

    private static (string pname, JsonElement schema, bool required) P(string name, string type, string desc, bool required = false, string? format = null, string? itemsType = null)
    {
        var o = new Dictionary<string, object?> { ["type"] = type, ["description"] = desc };
        if (format != null) o["format"] = format;
        // For array params (e.g. tags), declare the item type so the model emits the right shape.
        if (itemsType != null) o["items"] = new Dictionary<string, object?> { ["type"] = itemsType };
        return (name, ToElement(o), required);
    }

    private static JsonElement ToElement(object obj)
    {
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

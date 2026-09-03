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
public sealed partial class ChatToolDispatcher(
    ArticleService articleService,
    SearchService searchService,
    HybridSearchService hybridSearchService,
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

    /// <summary>Tool names that CAN be destructive (plan §2 Phase 3 "per-session destructive-op
    /// cap") — see <see cref="IsDestructiveTool"/> for the actual (args-aware) determination.
    /// <c>bee_delete_article</c>/<c>bee_replace_in_article</c> always count; <c>bee_update_article</c>
    /// only counts when its call actually carries a <c>content</c> argument.</summary>
    public static readonly IReadOnlySet<string> DestructiveTools = new HashSet<string>
    {
        "bee_delete_article", "bee_replace_in_article", "bee_update_article"
    };

    public static bool IsWriteTool(string name) => WriteTools.Contains(name);

    /// <summary>True if this specific call is destructive and must count against the per-conversation
    /// cap (see ChatDestructiveOpCounter). <c>bee_delete_article</c> and <c>bee_replace_in_article</c>
    /// always are. <c>bee_update_article</c> only is when it carries a <c>content</c> argument — that
    /// replaces the ENTIRE article body (no partial/no-op outcome, unlike replace's "0 occurrences"),
    /// exactly as destructive as bee_replace_in_article; a metadata-only update (title/treePath/tags,
    /// no content) is no more destructive than a rename and would otherwise burn the shared budget for
    /// free. Before this, the whole cap could be sidestepped by asking the model to "update" the body
    /// instead of "replace" it.</summary>
    public static bool IsDestructiveTool(string name, JsonElement args)
    {
        if (name is "bee_delete_article" or "bee_replace_in_article") return true;
        if (name == "bee_update_article")
            return args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String;
        return false;
    }

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
    /// ("AI wants to: &lt;summary&gt;"). Built from the tool name + the model's args; never throws.
    ///
    /// <para><b>M1 fix:</b> this used to deliberately omit <c>content</c>/<c>search</c>/<c>replace</c>
    /// — a human approved "Update article 3f2a…" with no sight of the payload, which is not a
    /// meaningful confirmation against a prompt-injected write (the injected instruction controls
    /// exactly the text nobody was shown). Every text-carrying write tool now includes a truncated,
    /// single-line preview of what will actually be written. It is still a PREVIEW, not the full
    /// body/diff — the confirm card is a short summary, not a document viewer — but a bounded
    /// snippet is enough for a human to recognize "this isn't what I asked for" the same way a git
    /// commit's diffstat is enough to smell a bad commit without reading the whole patch.</para></summary>
    public static string SummarizeWriteCall(string name, JsonElement args)
    {
        string Id() => args.TryGetProperty("id", out var e) ? (e.GetString() ?? "") : "";
        string Title() => args.TryGetProperty("title", out var e) ? (e.GetString() ?? "") : "";
        string Path() => args.TryGetProperty("treePath", out var e) ? (e.GetString() ?? "") : "";

        // Truncated, single-line preview of a string argument (content/search/replace). Collapses
        // real newlines to a visible " ↵ " marker rather than relying on the confirm card's HTML
        // to render literal line breaks (it doesn't — see chat.js, a plain innerHTML div), so a
        // multi-line body still reads as one coherent preview line instead of silently losing its
        // line breaks to HTML whitespace collapsing.
        string Preview(string prop, int maxLen = 200)
        {
            if (!args.TryGetProperty(prop, out var e) || e.ValueKind != JsonValueKind.String) return "";
            var s = e.GetString() ?? "";
            var oneLine = s.Replace("\r\n", "\n").Replace('\n', '↵' /* ↵ */);
            return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "…";
        }

        return name switch
        {
            "bee_save_article" => string.IsNullOrWhiteSpace(Title())
                ? $"Create a new article{(string.IsNullOrWhiteSpace(Path()) ? "" : " in " + Path())}: \"{Preview("content")}\""
                : $"Create article '{Title()}'{(string.IsNullOrWhiteSpace(Path()) ? "" : " in " + Path())}: \"{Preview("content")}\"",
            "bee_update_article" => args.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.String
                ? $"Update article {Id()}{(string.IsNullOrWhiteSpace(Title()) ? "" : " → '" + Title() + "'")} — REPLACES THE ENTIRE BODY with: \"{Preview("content")}\""
                : $"Update article {Id()}{(string.IsNullOrWhiteSpace(Title()) ? "" : " → '" + Title() + "'")} (metadata only — title/path/tags, no body change)",
            "bee_append_to_article" => $"Append to article {Id()}: \"{Preview("text")}\"",
            "bee_replace_in_article" => $"In article {Id()}, replace \"{Preview("search", 100)}\" with \"{Preview("replace", 100)}\"",
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

    // ── shared helpers ──────────────────────────────────────────────────────

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

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static string OkJson(string message, Guid? id = null)
    {
        var o = new Dictionary<string, object?> { ["ok"] = true, ["message"] = message };
        if (id.HasValue) o["id"] = id.Value;
        return JsonSerializer.Serialize(o, JsonOpts);
    }
}

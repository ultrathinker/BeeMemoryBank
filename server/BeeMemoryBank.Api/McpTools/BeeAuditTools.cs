using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeAuditTools(
    IEventLogRepository eventRepo,
    IArticleRepository articleRepo,
    IWhitelistRepository whitelistRepo,
    INodeIdentityRepository nodeRepo,
    IHttpContextAccessor httpContextAccessor,
    CallerScopeHolder scopeHolder,
    McpResponseManager responseManager)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private CallerIdentity GetCaller()
    {
        var ctx = httpContextAccessor.HttpContext;
        return ctx != null ? CallerIdentity.Extract(ctx) : new CallerIdentity(null, null, null, false);
    }

    [McpServerTool(Name = "bee_get_log")]
    [Description(
        "Query the activity log. Returns recent operations with actor and node info.\n" +
        "All parameters are optional filters. eventType filter is applied at the SQL level, " +
        "so limit counts only matching rows (not 'first N rows then filter').\n" +
        "By default only article-tied events plus folder_create/folder_rename/folder_delete are " +
        "returned (article_create/update/delete, comment_*, media_*, folder_*). Pass " +
        "includeAdminEvents=true to also see whitelist_*, " +
        "hard_delete, dek_rotation_*, restore_network, snapshot_checkpoint, etc. " +
        "includeAdminEvents requires superadmin scope; non-superadmin callers receive " +
        "the article-only view regardless of the parameter.")]
    public async Task<string> GetLog(
        [Description("Filter by article ID (GUID).")] Guid? articleId = null,
        [Description("Filter by event type, e.g. article_create, article_update, article_delete. Applied at SQL level.")] string? eventType = null,
        [Description("Maximum entries to return (default 50, max 200).")] int limit = 50,
        [Description("Entries to skip for pagination.")] int offset = 0,
        [Description("If true AND caller is superadmin, also include non-article events (whitelist, hard_delete, dek_rotation, restore_network, snapshot_checkpoint).")] bool includeAdminEvents = false)
    {
        var includeAdmin = includeAdminEvents && GetCaller().IsSuperadmin;
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        List<SyncEvent> events;

        if (articleId.HasValue)
        {
            events = await eventRepo.GetByArticleAsync(articleId.Value, limit);
            if (!string.IsNullOrEmpty(eventType))
                events = events.Where(e => e.EventType == eventType).ToList();
        }
        else
        {
            events = await eventRepo.GetRecentAsync(limit, offset, eventType);
        }

        var nodeIds = events.Select(e => e.NodeId).Distinct().ToHashSet();
        var nodeNames = new Dictionary<Guid, string>();
        var localIdentity = await nodeRepo.GetAsync();
        foreach (var nid in nodeIds)
        {
            if (localIdentity != null && nid == localIdentity.NodeId)
            {
                nodeNames[nid] = localIdentity.DisplayName;
                continue;
            }
            var entry = await whitelistRepo.GetByNodeIdAsync(nid);
            if (entry != null)
                nodeNames[nid] = entry.DisplayName;
        }

        var artIds = events.Where(e => e.ArticleId.HasValue).Select(e => e.ArticleId!.Value).Distinct().ToHashSet();
        var articleMeta = new Dictionary<Guid, string>();
        foreach (var id in artIds)
        {
            var article = await articleRepo.GetByIdAsync(id, includeDeleted: true);
            if (article != null)
                articleMeta[id] = article.Title;
        }

        var items = events
            .Where(e =>
            {
                // Always show events tied to an article whose metadata we could resolve.
                // Folder events (folder_create/rename/delete) carry no ArticleId but are not
                // admin-sensitive -- show them in the default view like article_* events,
                // subject to the same folder-scope ACL as everything else (EntityId is the
                // folder path for these event types -- see EventLogger.LogFolder*Async).
                // For superadmin + includeAdminEvents=true, also include the remaining
                // non-article events (whitelist, hard_delete, dek_rotation_*, restore_network,
                // snapshot_*).
                if (e.ArticleId.HasValue) return articleMeta.ContainsKey(e.ArticleId.Value);
                if (e.EventType.StartsWith("folder_", StringComparison.Ordinal))
                    return !scopeHolder.Scope.IsAccessDenied(e.EntityId);
                return includeAdmin;
            })
            .Select(e =>
            {
                nodeNames.TryGetValue(e.NodeId, out var nodeName);
                string? artTitle = null;
                if (e.ArticleId.HasValue && articleMeta.TryGetValue(e.ArticleId.Value, out var title))
                    artTitle = title;

                return new
                {
                    timestamp = e.CreatedAt,
                    eventType = e.EventType,
                    articleId = e.ArticleId,
                    articleTitle = artTitle,
                    nodeId = e.NodeId,
                    nodeName = nodeName ?? "unknown",
                    actorType = e.ActorType ?? "unknown",
                    actorName = e.ActorName,
                    viaAgentName = e.ViaAgentName
                };
            }).ToList();

        var json = JsonSerializer.Serialize(new { entries = items, total = items.Count, offset, limit }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }
}

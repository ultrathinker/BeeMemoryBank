using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
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
    FolderAccessService folderAccess,
    IHttpContextAccessor httpContextAccessor,
    McpResponseManager responseManager)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private int? GetAgentId()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent)
            return agent.Id;
        return null;
    }

    [McpServerTool(Name = "bee_get_log")]
    [Description(
        "Query the activity log. Returns recent operations with actor and node info.\n" +
        "All parameters are optional filters.")]
    public async Task<string> GetLog(
        [Description("Filter by article ID (GUID).")] Guid? articleId = null,
        [Description("Filter by event type: article_create, article_update, article_delete, comment_create, etc.")] string? eventType = null,
        [Description("Maximum entries to return (default 50, max 200).")] int limit = 50,
        [Description("Entries to skip for pagination.")] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        List<BeeMemoryBank.Core.Models.SyncEvent> events;

        if (articleId.HasValue)
        {
            events = await eventRepo.GetByArticleAsync(articleId.Value, limit);
        }
        else
        {
            events = await eventRepo.GetRecentAsync(limit, offset);
        }

        if (!string.IsNullOrEmpty(eventType))
            events = events.Where(e => e.EventType == eventType).ToList();

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);

        var nodeIds = events.Select(e => e.NodeId).Distinct().ToHashSet();
        var nodeNames = new Dictionary<Guid, string>();
        foreach (var nid in nodeIds)
        {
            var entry = await whitelistRepo.GetByNodeIdAsync(nid);
            if (entry != null)
                nodeNames[nid] = entry.DisplayName;
        }

        var artIds = events.Where(e => e.ArticleId.HasValue).Select(e => e.ArticleId!.Value).Distinct().ToHashSet();
        var articleMeta = new Dictionary<Guid, (string title, string? treePath)>();
        foreach (var id in artIds)
        {
            var article = await articleRepo.GetByIdAsync(id, includeDeleted: true);
            if (article != null)
                articleMeta[id] = (article.Title, article.TreePath);
        }

        var items = events
            .Where(e =>
            {
                if (restricted.Count == 0 || !e.ArticleId.HasValue)
                    return true;
                if (articleMeta.TryGetValue(e.ArticleId.Value, out var meta))
                    return !FolderAccessService.IsPathRestricted(restricted, meta.treePath);
                return true;
            })
            .Select(e =>
            {
                nodeNames.TryGetValue(e.NodeId, out var nodeName);
                string? artTitle = null;
                if (e.ArticleId.HasValue && articleMeta.TryGetValue(e.ArticleId.Value, out var meta))
                    artTitle = meta.title;

                return new
                {
                    timestamp = e.CreatedAt,
                    eventType = e.EventType,
                    articleId = e.ArticleId,
                    articleTitle = artTitle,
                    nodeId = e.NodeId,
                    nodeName = nodeName ?? "unknown",
                    actorType = e.ActorType ?? "unknown",
                    actorName = e.ActorName
                };
            }).ToList();

        var json = JsonSerializer.Serialize(new { entries = items, total = items.Count, offset, limit }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }
}

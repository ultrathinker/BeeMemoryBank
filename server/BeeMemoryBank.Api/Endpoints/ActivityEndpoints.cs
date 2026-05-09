using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/activity", async (
            IEventLogRepository eventRepo,
            IArticleRepository articleRepo,
            IWhitelistRepository whitelistRepo,
            INodeIdentityRepository nodeRepo,
            HttpContext ctx,
            int limit = 50,
            int offset = 0,
            Guid? articleId = null) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            limit = Math.Clamp(limit, 1, 200);
            offset = Math.Max(0, offset);

            List<BeeMemoryBank.Core.Models.SyncEvent> events;
            int total;

            if (articleId.HasValue)
            {
                events = await eventRepo.GetByArticleAsync(articleId.Value, limit);
                total = events.Count;
            }
            else
            {
                events = await eventRepo.GetRecentAsync(limit, offset);
                total = await eventRepo.GetTotalCountAsync();
            }

            // Resolve article titles
            var articleIds = events
                .Where(e => e.ArticleId.HasValue)
                .Select(e => e.ArticleId!.Value)
                .Distinct()
                .ToHashSet();

            var articleMeta = new Dictionary<Guid, (string Title, string TreePath)>();
            foreach (var id in articleIds)
            {
                var article = await articleRepo.GetByIdAsync(id, includeDeleted: true);
                if (article != null)
                    articleMeta[id] = (article.Title, article.TreePath);
            }

            // Resolve node names. Local node lives in tbl_node_identity only
            // (never in tbl_whitelist), so we need a fallback for self.
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

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);

            var items = events
                .Where(e => isSuperadmin || (e.ArticleId.HasValue && articleMeta.ContainsKey(e.ArticleId.Value)))
                .Select(e =>
                {
                    string? title = null;
                    string? path = null;
                    if (e.ArticleId.HasValue && articleMeta.TryGetValue(e.ArticleId.Value, out var meta))
                    {
                        title = meta.Title;
                        path = meta.TreePath;
                    }
                    nodeNames.TryGetValue(e.NodeId, out var nodeName);
                    return new ActivityItem(
                        e.EventType, e.ArticleId, title, path, e.CreatedAt,
                        e.NodeId, nodeName, e.ActorType, e.ActorName, e.ViaAgentName);
                }).ToList();

            var responseTotal = isSuperadmin ? total : items.Count;
            return Results.Ok(new ActivityResponse(items, responseTotal, offset, limit));
        }).WithTags("Activity");
    }
}

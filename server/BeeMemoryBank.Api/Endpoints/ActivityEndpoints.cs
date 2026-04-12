using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
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
            FolderAccessService folderAccess,
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

            // Resolve node names
            var nodeIds = events.Select(e => e.NodeId).Distinct().ToHashSet();
            var nodeNames = new Dictionary<Guid, string>();
            foreach (var nid in nodeIds)
            {
                var entry = await whitelistRepo.GetByNodeIdAsync(nid);
                if (entry != null)
                    nodeNames[nid] = entry.DisplayName;
            }

            var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);
            HashSet<string>? restrictedPaths = null;
            if (!isSuperadmin)
                restrictedPaths = await folderAccess.GetRestrictedPathsAsync(userId, agentId);

            var items = events
                .Where(e =>
                {
                    if (isSuperadmin || restrictedPaths == null || restrictedPaths.Count == 0)
                        return true;
                    if (!e.ArticleId.HasValue)
                        return true;
                    if (articleMeta.TryGetValue(e.ArticleId.Value, out var meta))
                        return !FolderAccessService.IsPathRestricted(restrictedPaths, meta.TreePath);
                    return true;
                })
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
                        e.NodeId, nodeName, e.ActorType, e.ActorName);
                }).ToList();

            return Results.Ok(new ActivityResponse(items, total, offset, limit));
        }).WithTags("Activity");
    }
}

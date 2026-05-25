using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Services;

public class FolderAccessService
{
    private static readonly ConcurrentDictionary<string, (HashSet<string> denyPaths, HashSet<string> allowPaths, HashSet<string> readOnlyPaths, DateTime loadedAt)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _serviceProvider;

    public FolderAccessService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<(HashSet<string> denyPaths, HashSet<string> allowPaths)> GetAccessInfoAsync(int? userId, int? agentId = null)
    {
        var (deny, allow, _) = await GetFullAccessInfoAsync(userId);
        return (deny, allow);
    }

    public async Task<(HashSet<string> denyPaths, HashSet<string> allowPaths, HashSet<string> readOnlyPaths)> GetFullAccessInfoAsync(int? userId)
    {
        if (userId is null)
            return ([], [], []);

        var cacheKey = $"u:{userId}";

        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.loadedAt < CacheTtl)
            return (cached.denyPaths, cached.allowPaths, cached.readOnlyPaths);

        var repo = _serviceProvider.GetRequiredService<IFolderAclRepository>();
        var folderRepo = _serviceProvider.GetRequiredService<IFolderRepository>();

        var entries = await repo.GetByUserIdAsync(userId.Value);

        var denyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readOnlyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var holder = _serviceProvider.GetRequiredService<CallerScopeHolder>();
        var previousScope = holder.Scope;
        holder.Scope = SystemCallerScope.Instance;
        try
        {
            foreach (var entry in entries)
            {
                var folder = await folderRepo.GetByIdAsync(entry.FolderId, includeDeleted: true);
                if (folder is null) continue;
                if (entry.Effect == AclEffect.Deny)
                {
                    denyPaths.Add(folder.Path);
                }
                else
                {
                    allowPaths.Add(folder.Path);
                    if (entry.IsReadOnly)
                        readOnlyPaths.Add(folder.Path);
                }
            }
        }
        finally
        {
            holder.Scope = previousScope;
        }

        _cache[cacheKey] = (denyPaths, allowPaths, readOnlyPaths, DateTime.UtcNow);
        return (denyPaths, allowPaths, readOnlyPaths);
    }

    public static bool IsAccessDenied(HashSet<string> denyPaths, HashSet<string> allowPaths, string? treePath)
    {
        if (string.IsNullOrEmpty(treePath))
            return true;

        // 1. Deny wins: if path matches any deny prefix → denied
        if (MatchesAnyPrefix(treePath, denyPaths))
            return true;

        // 2. If no allow rows exist → no restrictions (sees everything)
        if (allowPaths.Count == 0)
            return false;

        // 3. Allow list is non-empty: path must match an allow prefix
        return !MatchesAnyPrefix(treePath, allowPaths);
    }

    // True when any write operation (create/update/delete/move/rename) on
    // treePath should be refused for this caller. Reuses IsAccessDenied as
    // the floor: anything denied for read is also denied for write. On top,
    // matching an allow-row with is_read_only=1 also yields "write denied".
    public static bool IsWriteDenied(
        HashSet<string> denyPaths,
        HashSet<string> allowPaths,
        HashSet<string> readOnlyPaths,
        string? treePath)
    {
        if (IsAccessDenied(denyPaths, allowPaths, treePath))
            return true;
        if (string.IsNullOrEmpty(treePath))
            return true;
        return MatchesAnyPrefix(treePath, readOnlyPaths);
    }

    // True when treePath matches an allow-with-is_read_only=1 entry.
    // Used together with IsAccessDenied so endpoints can return a distinct
    // "read-only" error message instead of generic "no permission".
    public static bool IsReadOnlyForCaller(HashSet<string> readOnlyPaths, string? treePath)
    {
        if (string.IsNullOrEmpty(treePath))
            return false;
        return MatchesAnyPrefix(treePath, readOnlyPaths);
    }

    private static bool MatchesAnyPrefix(string treePath, HashSet<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (treePath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (prefix == "/")
                return true;
            if (treePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void InvalidateCache(int? userId)
    {
        if (userId is not null)
            _cache.TryRemove($"u:{userId}", out _);
    }

    public async Task InvalidateCacheForFolderAsync(Guid folderId)
    {
        var repo = _serviceProvider.GetRequiredService<IFolderAclRepository>();
        var userIds = await repo.GetUserIdsByFolderIdAsync(folderId);
        foreach (var userId in userIds)
            InvalidateCache(userId);
    }

    public async Task InvalidateCacheForFoldersAsync(IEnumerable<Guid> folderIds)
    {
        var repo = _serviceProvider.GetRequiredService<IFolderAclRepository>();
        var userIds = new HashSet<int>();
        foreach (var folderId in folderIds)
        {
            var ids = await repo.GetUserIdsByFolderIdAsync(folderId);
            foreach (var id in ids)
                userIds.Add(id);
        }
        foreach (var userId in userIds)
            InvalidateCache(userId);
    }

    public static List<Article> FilterArticles(List<Article> articles, HashSet<string> denyPaths, HashSet<string> allowPaths)
    {
        if (denyPaths.Count == 0 && allowPaths.Count == 0)
            return articles;

        return articles.Where(a => !IsAccessDenied(denyPaths, allowPaths, a.TreePath)).ToList();
    }

    public static List<Folder> FilterFolders(List<Folder> folders, HashSet<string> denyPaths, HashSet<string> allowPaths)
    {
        if (denyPaths.Count == 0 && allowPaths.Count == 0)
            return folders;

        return folders.Where(f => !IsAccessDenied(denyPaths, allowPaths, f.Path)).ToList();
    }

    // Returns the set of ancestor paths of each allowed path.
    // E.g. {"/Work/Project2"} → {"/", "/Work"}.
    // Ancestors are shown as empty navigation stubs in the folder tree so the user
    // can walk down to their allowed subtree without exposing sibling folders.
    public static HashSet<string> ComputeAncestors(HashSet<string> allowedPaths)
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/" };
        foreach (var path in allowedPaths)
        {
            if (string.IsNullOrEmpty(path) || path == "/") continue;
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.IndexOf('/', 1);
            while (idx > 0)
            {
                ancestors.Add(trimmed[..idx]);
                idx = trimmed.IndexOf('/', idx + 1);
            }
        }
        return ancestors;
    }
}

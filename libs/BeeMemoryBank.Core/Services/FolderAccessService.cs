using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Services;

public class FolderAccessService
{
    private static readonly ConcurrentDictionary<string, (HashSet<string> paths, DateTime loadedAt)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _serviceProvider;

    public FolderAccessService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<HashSet<string>> GetRestrictedPathsAsync(int? userId, int? agentId)
    {
        if (userId is null && agentId is null)
            return [];

        var cacheKey = userId is not null ? $"u:{userId}" : $"a:{agentId}";

        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.loadedAt < CacheTtl)
            return cached.paths;

        var restrictions = new List<FolderRestriction>();
        var repo = _serviceProvider.GetRequiredService<IFolderRestrictionRepository>();
        var folderRepo = _serviceProvider.GetRequiredService<IFolderRepository>();

        if (userId is not null)
            restrictions = await repo.GetByUserIdAsync(userId.Value);
        else if (agentId is not null)
            restrictions = await repo.GetByAgentIdAsync(agentId.Value);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in restrictions)
        {
            var folder = await folderRepo.GetByIdAsync(r.FolderId, includeDeleted: true);
            if (folder is not null)
                paths.Add(folder.Path);
        }

        _cache[cacheKey] = (paths, DateTime.UtcNow);
        return paths;
    }

    public static bool IsPathRestricted(HashSet<string> restrictedPaths, string? treePath)
    {
        if (string.IsNullOrEmpty(treePath) || treePath == "/")
            return false;

        foreach (var restricted in restrictedPaths)
        {
            if (treePath.Equals(restricted, StringComparison.OrdinalIgnoreCase))
                return true;
            if (treePath.StartsWith(restricted + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void InvalidateCache(int? userId, int? agentId)
    {
        if (userId is not null)
            _cache.TryRemove($"u:{userId}", out _);
        if (agentId is not null)
            _cache.TryRemove($"a:{agentId}", out _);
    }

    public static List<Article> FilterArticles(List<Article> articles, HashSet<string> restrictedPaths)
    {
        if (restrictedPaths.Count == 0)
            return articles;

        return articles.Where(a => !IsPathRestricted(restrictedPaths, a.TreePath)).ToList();
    }

    public static List<Folder> FilterFolders(List<Folder> folders, HashSet<string> restrictedPaths)
    {
        if (restrictedPaths.Count == 0)
            return folders;

        return folders.Where(f => !IsPathRestricted(restrictedPaths, f.Path)).ToList();
    }
}

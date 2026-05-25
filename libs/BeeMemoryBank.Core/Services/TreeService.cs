using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public class TreeService(IArticleRepository articleRepo, IFolderRepository folderRepo)
{
    public async Task<Dictionary<string, List<string>>> GetTreeAsync(bool includeEmptySystemFolders = false)
    {
        var folders = await folderRepo.GetAllActiveAsync();

        // Determine which system-folder roots are empty (no descendants and no
        // direct articles) — they get hidden from the tree to keep the UI quiet
        // until the first time backend code writes into them.
        HashSet<string> hiddenSystemPaths = [];
        if (!includeEmptySystemFolders)
        {
            var systemRoots = folders.Where(f => f.IsSystem).Select(f => f.Path).ToList();
            if (systemRoots.Count > 0)
            {
                var allArticles = await articleRepo.ListAsync();
                hiddenSystemPaths = systemRoots
                    .Where(root =>
                    {
                        var prefix = root.TrimEnd('/') + "/";
                        var hasSubfolder = folders.Any(f => f.Path != root && f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        var hasArticle = allArticles.Any(a =>
                        {
                            var p = a.TreePath ?? "/";
                            return p == root || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                        });
                        return !hasSubfolder && !hasArticle;
                    })
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        var tree = new Dictionary<string, List<string>>();

        foreach (var folder in folders)
        {
            if (hiddenSystemPaths.Contains(folder.Path)) continue;

            if (!tree.ContainsKey(folder.Path))
                tree[folder.Path] = [];

            var parentPath = folder.ParentPath;
            if (parentPath != null)
            {
                if (!tree.TryGetValue(parentPath, out var children))
                {
                    children = [];
                    tree[parentPath] = children;
                }
                if (!children.Contains(folder.Path))
                    children.Add(folder.Path);
            }
        }

        return tree;
    }

    public async Task<TreeChildrenResult> GetChildrenAsync(string path)
    {
        path = NormalizePath(path);

        var directArticles = (await articleRepo.ListAsync(path))
            .Where(a => NormalizePath(a.TreePath) == path)
            .OrderBy(a => a.Title, UnderscoreFirstComparer.Instance)
            .ToList();

        var parentPathForQuery = path == "/" ? null : path;
        var childFolders = await folderRepo.GetChildrenAsync(parentPathForQuery);

        var allArticles = await articleRepo.ListAsync();
        // For the system-folder hide check we need EVERY active folder, not just
        // direct siblings — checking inside childFolders alone (gemini round-3
        // bug) would always return false and hide non-empty `_Drafts` whenever
        // its content lives in subfolders rather than directly under it.
        var allFoldersForHide = await folderRepo.GetAllActiveAsync();
        var folders = childFolders
            .OrderBy(f => f.Name, UnderscoreFirstComparer.Instance)
            .Select(f => new FolderInfo
            {
                Id = f.Id,
                Path = f.Path,
                Name = f.Name,
                ArticleCount = allArticles.Count(a =>
                    NormalizePath(a.TreePath) == f.Path ||
                    NormalizePath(a.TreePath).StartsWith(f.Path.TrimEnd('/') + "/")),
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt,
                IsSystem = f.IsSystem,
                IsRemote = f.RemoteSubscriptionId.HasValue
            })
            .Where(f =>
            {
                if (!f.IsSystem) return true;
                // Hide empty system folders (no articles and no sub-folders).
                var prefix = f.Path.TrimEnd('/') + "/";
                var hasSub = allFoldersForHide.Any(cf => cf.Path != f.Path
                    && cf.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                return f.ArticleCount > 0 || hasSub;
            })
            .ToList();

        return new TreeChildrenResult
        {
            Path = path,
            Folders = folders,
            Articles = directArticles
        };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    public async Task<List<string>> GetUniquePathsAsync()
    {
        var folders = await folderRepo.GetAllActiveAsync();
        return folders.Select(f => f.Path).OrderBy(p => p).ToList();
    }
}

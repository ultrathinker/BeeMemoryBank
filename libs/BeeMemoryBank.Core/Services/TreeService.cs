using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public class TreeService(IArticleRepository articleRepo, IFolderRepository folderRepo)
{
    public async Task<Dictionary<string, List<string>>> GetTreeAsync()
    {
        var folders = await folderRepo.GetAllActiveAsync();
        var tree = new Dictionary<string, List<string>>();

        foreach (var folder in folders)
        {
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
                UpdatedAt = f.UpdatedAt
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

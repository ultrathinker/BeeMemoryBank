using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Mobile.Pages;

internal static class TreeHelpers
{
    public static List<TreeListItem> BuildItems(List<Article> articles, string currentPath)
    {
        // Collect all unique folder paths that are direct children of currentPath
        var childFolderPaths = new HashSet<string>();
        foreach (var article in articles)
        {
            var normalized = NormalizePath(article.TreePath);
            if (normalized == currentPath || normalized == "/") continue;
            if (IsDirectChildFolder(currentPath, normalized))
                childFolderPaths.Add(normalized);
        }

        var folderItems = childFolderPaths
            .Select(p => new TreeListItem(GetLastSegment(p), true, p, null, null))
            .OrderBy(i => i.Name, UnderscoreFirstComparer.Instance)
            .ToList();

        var articleItems = articles
            .Where(a => NormalizePath(a.TreePath) == currentPath)
            .OrderBy(a => a.Title, UnderscoreFirstComparer.Instance)
            .Select(a => new TreeListItem(a.Title, false, null, a.Id, a.UpdatedAt))
            .ToList();

        var result = new List<TreeListItem>(folderItems.Count + articleItems.Count);
        result.AddRange(folderItems);
        result.AddRange(articleItems);
        return result;
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    public static bool IsDirectChildFolder(string parentPath, string candidatePath)
    {
        if (parentPath == "/")
            return candidatePath != "/" && !candidatePath.TrimStart('/').Contains('/');
        var prefix = parentPath + "/";
        if (!candidatePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return !candidatePath[prefix.Length..].Contains('/');
    }

    public static string GetLastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed.TrimStart('/') : trimmed[(idx + 1)..];
    }
}

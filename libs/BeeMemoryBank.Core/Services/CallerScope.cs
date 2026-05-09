using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public sealed class SystemCallerScope : ICallerScope
{
    public static readonly SystemCallerScope Instance = new();

    public bool IsSuperadmin => true;

    public bool IsAccessDenied(string? treePath) => false;

    public bool IsNavigable(string? treePath) => true;

    public List<Article> FilterArticles(List<Article> articles) => articles;

    public List<Folder> FilterFolders(List<Folder> folders) => folders;
}

/// <summary>
/// Fail-closed scope. Returned when an HTTP request reaches repository code before
/// CallerScopeMiddleware has set a proper scope — i.e. "we don't know who you are,
/// so you see nothing." Never pick this scope explicitly; it's a safety net.
/// </summary>
public sealed class DenyAllScope : ICallerScope
{
    public static readonly DenyAllScope Instance = new();

    public bool IsSuperadmin => false;

    public bool IsAccessDenied(string? treePath) => true;

    public bool IsNavigable(string? treePath) => false;

    public List<Article> FilterArticles(List<Article> articles) => [];

    public List<Folder> FilterFolders(List<Folder> folders) => [];
}

public sealed class HttpCallerScope : ICallerScope
{
    private readonly HashSet<string> _denyPaths;
    private readonly HashSet<string> _allowPaths;
    private readonly HashSet<string> _ancestors;

    public bool IsSuperadmin { get; }

    public HttpCallerScope(bool isSuperadmin, HashSet<string> denyPaths, HashSet<string> allowPaths)
    {
        IsSuperadmin = isSuperadmin;
        _denyPaths = denyPaths;
        _allowPaths = allowPaths;
        _ancestors = allowPaths.Count > 0
            ? FolderAccessService.ComputeAncestors(allowPaths)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAccessDenied(string? treePath)
        => IsSuperadmin ? false : FolderAccessService.IsAccessDenied(_denyPaths, _allowPaths, treePath);

    public bool IsNavigable(string? treePath)
    {
        if (IsSuperadmin) return true;
        if (string.IsNullOrEmpty(treePath)) return false;
        if (!FolderAccessService.IsAccessDenied(_denyPaths, _allowPaths, treePath)) return true;
        return _ancestors.Contains(treePath);
    }

    public List<Article> FilterArticles(List<Article> articles)
        => IsSuperadmin ? articles : FolderAccessService.FilterArticles(articles, _denyPaths, _allowPaths);

    public List<Folder> FilterFolders(List<Folder> folders)
    {
        if (IsSuperadmin) return folders;
        return folders.Where(f => IsNavigable(f.Path)).ToList();
    }
}

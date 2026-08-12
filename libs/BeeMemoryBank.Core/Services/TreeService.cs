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

    /// <summary>
    /// Depth-bounded, optionally paginated tree view used by the <c>bee_get_tree</c> MCP tool.
    ///
    /// Reproduces the legacy <c>BeeReadTools.GetTree</c> inline build byte-for-byte when
    /// <paramref name="depth"/> and <paramref name="limit"/> are both null: folders ∪ article
    /// paths, optional <paramref name="path"/> subtree filter, alphabetical (default string)
    /// ordering, and <c>isSystem</c>/<c>isRemote</c> flags. It then layers on:
    ///  - <paramref name="depth"/>: caps how many path segments below the (optional) path filter
    ///    the response descends (null = unlimited). <paramref name="path"/> itself is always
    ///    level 0; its direct children are level 1, etc.
    ///  - <paramref name="limit"/> + <paramref name="offset"/>: bounds how many folder+article
    ///    entries a single call returns after depth filtering (null <paramref name="limit"/> = no
    ///    pagination, matching the pre-existing unbounded contract).
    ///
    /// ACL/scope filtering is unchanged: the article and folder repositories apply
    /// <c>CallerScopeHolder</c> ambiently, so every collection this method iterates is already
    /// scoped to the caller's navigable set. Depth/pagination compose with that filtering rather
    /// than bypassing or duplicating it.
    /// </summary>
    public async Task<TreePathsResult> GetTreePathsAsync(
        string? path = null,
        int? depth = null,
        int? limit = null,
        int offset = 0)
    {
        // Same fetch + scope semantics as the legacy BeeReadTools.GetTree inline implementation:
        // articles are path-filtered at the repo, folders come back as the full active set. Both
        // repos apply CallerScopeHolder filtering ambiently — do not re-filter here.
        var articles = await articleRepo.ListAsync(path);
        var folders = await folderRepo.GetAllActiveAsync();

        var articlesByPath = articles
            .GroupBy(a => a.TreePath)
            .ToDictionary(g => g.Key, g => g.Select(a => new TreePathArticleRef { Id = a.Id, Title = a.Title }).ToList());

        var folderMeta = folders.ToDictionary(f => f.Path, f => f);
        var allPaths = new HashSet<string>(folders.Select(f => f.Path));
        foreach (var a in articles)
            allPaths.Add(a.TreePath);

        // Legacy path subtree filter, preserved exactly (default/culture-sensitive StartsWith and
        // equality, mirroring the original BeeReadTools.GetTree). path == null => whole tree.
        string? pathPrefix = path != null ? path.TrimEnd('/') + "/" : null;
        bool PathFilterMatches(string p) =>
            pathPrefix == null || p == path || p.StartsWith(pathPrefix);

        IEnumerable<string> filtered = allPaths.Where(PathFilterMatches);

        if (depth.HasValue)
        {
            var maxDepth = depth.Value;
            var baseLevel = SegmentCount(path);
            // Keep the scoped path itself (level 0 relative to itself) plus `maxDepth` levels below.
            filtered = filtered.Where(p => SegmentCount(p) - baseLevel <= maxDepth);
        }

        // OrderBy(p => p) deliberately uses Comparer<string>.Default to match the legacy ordering
        // byte-for-byte — switching to an explicit comparer would reorder entries for non-ASCII paths.
        var ordered = filtered.OrderBy(p => p).ToList();
        var total = ordered.Count;

        var appliedOffset = Math.Max(0, offset);
        int? appliedLimit = limit.HasValue ? Math.Max(1, limit.Value) : null;

        List<string> pagePaths;
        bool truncated;
        if (appliedLimit.HasValue)
        {
            pagePaths = ordered.Skip(appliedOffset).Take(appliedLimit.Value).ToList();
            truncated = appliedOffset + pagePaths.Count < total;
        }
        else
        {
            // No pagination: return everything after depth filtering. We still honor a non-zero
            // offset (skip) for completeness, but a caller passing offset without limit never gets
            // an empty page — the unbounded contract is preserved.
            pagePaths = appliedOffset > 0 ? ordered.Skip(appliedOffset).ToList() : ordered;
            truncated = false;
        }

        var entries = pagePaths.Select(p =>
        {
            folderMeta.TryGetValue(p, out var meta);
            return new TreePathEntry
            {
                Path = p,
                IsSystem = meta?.IsSystem ?? false,
                IsRemote = meta?.RemoteSubscriptionId.HasValue ?? false,
                Articles = articlesByPath.TryGetValue(p, out var arts) ? arts : []
            };
        }).ToList();

        return new TreePathsResult
        {
            Paths = entries,
            Depth = depth,
            Limit = appliedLimit,
            Offset = appliedOffset,
            Total = total,
            Truncated = truncated
        };
    }

    /// <summary>Number of non-empty path segments ("/" → 0, "/Work" → 1, "/Work/Infra" → 2).</summary>
    private static int SegmentCount(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return 0;
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return 0;
        var count = 0;
        foreach (var c in trimmed)
            if (c == '/') count++;
        return count + 1;
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

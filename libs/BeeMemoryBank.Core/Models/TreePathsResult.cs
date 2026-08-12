namespace BeeMemoryBank.Core.Models;

public class TreePathArticleRef
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

public class TreePathEntry
{
    public string Path { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool IsRemote { get; set; }
    public List<TreePathArticleRef> Articles { get; set; } = [];
}

/// <summary>
/// Depth-bounded, optionally paginated tree view returned by
/// <see cref="Services.TreeService.GetTreePathsAsync"/> and consumed by the
/// <c>bee_get_tree</c> MCP tool. Entries are sorted alphabetically by path.
/// </summary>
public class TreePathsResult
{
    /// <summary>Folder + article-path entries for the requested page, ordered alphabetically.</summary>
    public List<TreePathEntry> Paths { get; set; } = [];

    /// <summary>Applied depth limit (null = unlimited). Echoed for the caller's convenience.</summary>
    public int? Depth { get; set; }

    /// <summary>Applied page-size limit (null = no pagination; all matching entries returned).</summary>
    public int? Limit { get; set; }

    /// <summary>Applied offset (entries skipped before the returned page).</summary>
    public int Offset { get; set; }

    /// <summary>
    /// Total number of entries matching the path + depth filters BEFORE pagination. Use this to
    /// decide whether more <c>offset</c> pages exist (more remain while offset + Paths.Count &lt; Total).
    /// </summary>
    public int Total { get; set; }

    /// <summary>True when more entries remain beyond the returned page (i.e. another page follows).</summary>
    public bool Truncated { get; set; }
}

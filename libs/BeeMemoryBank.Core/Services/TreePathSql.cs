namespace BeeMemoryBank.Core.Services;

/// <summary>
/// SQL for "this folder and everything under it", matched exactly as folder identity is defined:
/// case-sensitive, byte for byte (<c>tbl_folder.path</c> is BINARY and its unique index is
/// case-sensitive, so "/Work" and "/work" are two different folders).
///
/// <para>
/// Never express a subtree with <c>LIKE 'prefix/%'</c>: SQLite's LIKE folds ASCII case, so renaming,
/// deleting or hard-deleting "/Work" would also reach "/work/…". The strict descendants of a path are
/// exactly the strings in the half-open BINARY range ["path/", "path0"), because '0' is the byte that
/// follows '/'. The range needs no wildcard escaping and can use an index.
/// </para>
/// </summary>
public static class TreePathSql
{
    /// <summary>Bounds of the range holding exactly the strict descendants of <paramref name="path"/>.</summary>
    public static (string Lower, string Upper) DescendantRange(string path)
    {
        var trimmed = path.TrimEnd('/');
        return (trimmed + "/", trimmed + "0");
    }

    /// <summary><c>(column &gt;= @lower AND column &lt; @upper)</c> — strict descendants only.</summary>
    public static string DescendantPredicate(string column, string lowerParam, string upperParam) =>
        $"({column} >= @{lowerParam} AND {column} < @{upperParam})";
}

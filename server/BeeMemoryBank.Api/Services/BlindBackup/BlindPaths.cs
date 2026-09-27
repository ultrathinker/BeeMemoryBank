namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// Where backup data may live relative to the node's own volume. The node deletes inside its
/// data path on purpose — the staging copy at startup, the restic cache and everything else on
/// "Disconnect and wipe" — so a repository (or a one-off copy) placed anywhere in it, or around
/// it, would be destroyed by exactly the operations that promise never to touch backups.
/// </summary>
public static class BlindPaths
{
    private static StringComparison Cmp =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Null when <paramref name="path"/> is outside the node's data path; otherwise the reason,
    /// for the operator. Both sides are compared after resolving symlinks, so a link from
    /// /backups/x into /app/data does not get past the check.
    /// </summary>
    public static string? OutsideNodeData(string path, string nodeDataPath, string what)
    {
        if (!Path.IsPathFullyQualified(path))
            return $"the {what} must be an absolute path";
        var target = RealPath(path);
        var data = RealPath(nodeDataPath);
        if (target is null || data is null)
            return $"the {what} {path} goes through a symlink that cannot be resolved (a loop, or no permission to read it)";
        return Overlaps(target, data)
            ? $"the {what} {path} overlaps the node's data folder {nodeDataPath} — the wipe and the " +
              "staging cleanup delete there; choose a folder outside it (e.g. under /backups)"
            : null;
    }

    /// <summary>One path is the other, or lies inside it — in either direction.</summary>
    public static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(a);
        b = Path.TrimEndingDirectorySeparator(b);
        return string.Equals(a, b, Cmp) || IsUnder(a, b) || IsUnder(b, a);
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir + Path.DirectorySeparatorChar, Cmp);

    /// <summary>
    /// The absolute path with every symlink component resolved — including a link whose target
    /// does not exist yet (a dangling link outside the data folder that points into it would land
    /// the repository in the node volume once the target is created). The link is read itself,
    /// not through Exists, so this does not depend on how the platform reports a dangling link.
    /// The part of the path that does not exist is kept as written (a repository folder is
    /// usually created on first backup). Null when a link cannot be resolved: a loop, or a link
    /// that cannot be read.
    /// </summary>
    public static string? RealPath(string path, int depth = 0)
    {
        if (depth > 32) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            string? link;
            try
            {
                // LinkTarget reads the link itself (lstat/readlink): it answers for a dangling
                // link too, where Exists — which follows the link — says false.
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                link = info.LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (link is null)
            {
                current = next;
                continue;
            }
            var resolved = RealPath(Path.IsPathRooted(link) ? link : Path.Combine(current, link), depth + 1);
            if (resolved is null) return null;
            current = resolved;
        }
        return current;
    }
}

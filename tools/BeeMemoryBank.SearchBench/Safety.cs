namespace BeeMemoryBank.SearchBench;

/// <summary>
/// Safety gate over the benchmark data directory. The harness points a real <c>BeeMemoryBank.Api</c>
/// process at a directory and unlocks it — so a path that happens to be a real user vault would let
/// the benchmark read (and, via the seed step, potentially write) real private data. This type
/// enforces two layers of refusal:
/// <list type="bullet">
///   <item><b>Hard refusal</b> — the path matches a real-install signature (the well-known
///       <c>BeeMemoryBankData</c> root, a default vault dir, a user-profile root, etc.). This is
///       NEVER overridden, not even by <c>--allow-data-path</c>.</item>
///   <item><b>Soft refusal</b> — the path doesn't look scratch-like (not under the system temp dir,
///       no benchmark marker segment). Overridable with <c>--allow-data-path</c> for users who
///       keep their scratch dirs somewhere deliberate.</item>
/// </list>
/// </summary>
internal static class PathSafety
{
    /// <summary>Path segments that mark a directory as an intentional benchmark scratch space.</summary>
    private static readonly HashSet<string> ScratchMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "searchbench", "bmb-searchbench", "search-bench", "bmb-bench", "bench-scratch", "bmb-scratch"
    };

    /// <summary>Path segments that are unambiguous signatures of a real BeeMemoryBank install.</summary>
    private static readonly HashSet<string> RealInstallSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "BeeMemoryBankData"
    };

    /// <summary>User-profile subfolders that are obviously not scratch space.</summary>
    private static readonly HashSet<string> ProtectedUserProfileChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music", "OneDrive"
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns a non-null reason string if <paramref name="rawPath"/> matches a real-install
    /// signature. The caller MUST refuse to run in that case, regardless of any override flag.
    /// </summary>
    public static string? HardRefusalReason(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return "Data path is empty.";

        string full;
        try { full = Path.GetFullPath(rawPath.Trim()); }
        catch (Exception ex) { return $"Data path '{rawPath}' is not a valid path: {ex.Message}"; }

        // Any path segment that literally matches a real-install signature.
        foreach (var seg in EnumerateSegments(full))
            if (RealInstallSegments.Contains(seg))
                return $"'{full}' is inside a real BeeMemoryBank install root (segment '{seg}').";

        // At or under the default vault directory, or any platform's well-known data root.
        foreach (var root in GetWellKnownRoots())
            if (IsWithin(full, root))
                return $"'{full}' is under the well-known BeeMemoryBank data root '{root}'.";

        // The bare user profile and its obvious document/media children.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            string profileFull = Path.GetFullPath(profile);
            string fullTrimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullTrimmed, profileFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison))
                return $"'{full}' is the user profile directory — refusing to use it as benchmark data.";

            if (IsWithin(full, profileFull))
            {
                var relative = Path.GetRelativePath(profileFull, full);
                var firstSeg = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0];
                if (ProtectedUserProfileChildren.Contains(firstSeg))
                    return $"'{full}' is inside the user '{firstSeg}' folder — refusing to use it as benchmark data.";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="rawPath"/> looks like an intentional scratch location:
    /// under the system temp dir, or containing a benchmark marker segment.
    /// </summary>
    public static bool IsScratchLike(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        string full;
        try { full = Path.GetFullPath(rawPath.Trim()); }
        catch { return false; }

        if (IsWithin(full, Path.GetFullPath(Path.GetTempPath())))
            return true;

        foreach (var seg in EnumerateSegments(full))
            if (ScratchMarkers.Contains(seg))
                return true;

        return false;
    }

    /// <summary>A scratch directory under the system temp root, suitable as a default data path.</summary>
    public static string DefaultScratchDir(string label)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(Path.GetTempPath(), "bmb-searchbench", $"{label}-{stamp}");
    }

    /// <summary>Walks every path segment (directory name) of <paramref name="fullPath"/>, root excluded.</summary>
    private static IEnumerable<string> EnumerateSegments(string fullPath)
    {
        var dir = fullPath;
        var seen = new HashSet<string>();
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name))
                break; // reached a root like "C:\" or "/"
            if (seen.Add(dir))
                yield return name;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }
    }

    /// <summary>True if <paramref name="path"/> equals or is nested under <paramref name="prefix"/>.</summary>
    private static bool IsWithin(string path, string prefix)
    {
        string p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string pre = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (p.Length < pre.Length)
            return false;
        if (string.Equals(p, pre, PathComparison))
            return true;
        return p.Length > pre.Length &&
               p[pre.Length] is var sep && (sep == Path.DirectorySeparatorChar || sep == Path.AltDirectorySeparatorChar) &&
               p.StartsWith(pre, PathComparison);
    }

    private static List<string> GetWellKnownRoots()
    {
        var roots = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var lad = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(lad)) roots.Add(Path.Combine(lad, "BeeMemoryBankData"));
                var ad = Environment.GetEnvironmentVariable("APPDATA");
                if (!string.IsNullOrEmpty(ad)) roots.Add(Path.Combine(ad, "BeeMemoryBankData"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                    roots.Add(Path.Combine(home, "Library", "Application Support", "BeeMemoryBankData"));
            }
            else
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                    roots.Add(Path.Combine(home, ".local", "share", "BeeMemoryBankData"));
            }
        }
        catch { /* ignore — best-effort detection */ }
        return roots;
    }
}

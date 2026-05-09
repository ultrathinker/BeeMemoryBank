namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Normalises BeeMemoryBank "tree paths" (logical folder paths like "/Work/Project1")
/// for storage. Defensive layer to keep things consistent across:
///   - direct API/MCP writes (folder/article create/move/rename)
///   - sync events from peers (a malicious or buggy peer could push odd paths)
///   - bulk imports (Obsidian zip, snapshot restore)
///
/// Canonical form:
///   - "/" for empty / null / "/"
///   - leading "/", no trailing "/", no consecutive "/"
///   - no "." or ".." segments (rejected — there is no real filesystem here,
///     so they would just create folders with literal odd names that pollute
///     the namespace and confuse prefix-based ACL matching).
///   - control characters and NUL rejected.
/// </summary>
public static class TreePathCanonicalizer
{
    /// <summary>Canonicalises a tree path. Throws ArgumentException for invalid input.</summary>
    public static string Canonicalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        if (path == "/") return "/";

        var segments = path.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "/";

        foreach (var seg in segments)
        {
            if (seg == "." || seg == "..")
                throw new System.ArgumentException(
                    $"Tree path may not contain '.' or '..' segments: '{path}'");
            foreach (var c in seg)
            {
                if (c < 0x20 || c == 0x7F)
                    throw new System.ArgumentException(
                        $"Tree path may not contain control characters: '{path}'");
            }
        }

        return "/" + string.Join("/", segments);
    }

    /// <summary>
    /// Like <see cref="Canonicalize"/> but returns null for invalid input
    /// instead of throwing — useful in sync apply paths where we want to
    /// silently drop a peer's malformed event rather than crash the loop.
    /// </summary>
    public static string? TryCanonicalize(string? path)
    {
        try { return Canonicalize(path); }
        catch (System.ArgumentException) { return null; }
    }

    /// <summary>
    /// Strict-only check: true if the path contains "." / ".." segments,
    /// control characters, or NUL. Cosmetic non-canonical input ("//" or
    /// trailing "/") is NOT treated as illegal — those should be
    /// canonicalised at write paths, but allowed through at sync apply
    /// paths so we don't permanently diverge from peers running older
    /// (pre-canonicalisation) code that may have legitimately produced
    /// such paths in their history.
    /// </summary>
    public static bool IsIllegal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var segments = path.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg == "." || seg == "..") return true;
            foreach (var c in seg)
                if (c < 0x20 || c == 0x7F) return true;
        }
        return false;
    }
}

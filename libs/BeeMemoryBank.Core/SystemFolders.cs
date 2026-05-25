namespace BeeMemoryBank.Core;

/// <summary>
/// Registry of reserved folder names protected by the service layer.
/// Centralised so that future system folders (e.g. _Trash, _Inbox) can be added
/// by adding a single constant + entry in <see cref="ReservedRootNames"/>.
/// </summary>
public static class SystemFolders
{
    // Container used by the app for drafts, recovered remote content,
    // conflict-resolution snapshots, and failed offline-save fallbacks.
    public const string Drafts = "_Drafts";

    public const string DraftsRemote = "_Drafts/remote";
    public const string DraftsLocal = "_Drafts/local";
    public const string DraftsRestored = "_Drafts/restored";

    // Ordinal (case-sensitive) on purpose: we want to allow the user to keep a
    // lower-case "_drafts" folder if they happen to have one; only the exact
    // capitalisation "_Drafts" is reserved.
    public static readonly IReadOnlySet<string> ReservedRootNames =
        new HashSet<string>(StringComparer.Ordinal) { Drafts };

    /// <summary>
    /// True when the path matches a reserved system folder root.
    /// Only the root (e.g. "/_Drafts") is protected; sub-folders such as
    /// "/_Drafts/local" are regular folders and can be created / deleted freely.
    /// </summary>
    public static bool IsReservedSystemPath(string? treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath)) return false;
        var trimmed = treePath.TrimEnd('/');
        if (!trimmed.StartsWith('/')) return false;
        var name = trimmed[1..];
        return ReservedRootNames.Contains(name);
    }
}

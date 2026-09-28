namespace BeeMemoryBank.AppPaths;

/// <summary>
/// The locks of an offline re-key (rekey-offline.md §2 step 0).
/// <list type="bullet">
/// <item><c>&lt;D&gt;.rekey.lock</c>, next to D: held open with <see cref="FileShare.None"/> by the <c>bmb rekey</c> verb
///   for its whole run. A normal start refuses while it is held. A lock file nobody holds is left by a verb that
///   died; it does not stop a start, and the next run of the verb discards what that one left.</item>
/// <item><c>D/node.lock</c>: the node's own lock (<c>DirectoryLock</c>). The verb refuses while a node holds it, and
///   then holds it itself, so a node cannot start in the middle of a re-key.</item>
/// </list>
/// <see cref="FileShare.None"/> is an OS lock on Windows and an <c>flock</c> on Linux and macOS; both go with the
/// process.
/// </summary>
public static class RekeyLock
{
    /// <summary>Holds <c>&lt;D&gt;.rekey.lock</c>; null when another process holds it.</summary>
    public static IDisposable? TryAcquire(string dataDir) =>
        TryOpenExclusive(RekeySwapJournal.LockPathFor(dataDir), FileOptions.None);

    /// <summary>Holds <c>D/node.lock</c> exactly as the node does; null when a node holds it.</summary>
    public static IDisposable? TryAcquireNodeLock(string dataDir) =>
        TryOpenExclusive(Path.Combine(RekeySwapJournal.Normalize(dataDir), "node.lock"), FileOptions.DeleteOnClose);

    /// <summary>Whether a running re-key holds <c>&lt;D&gt;.rekey.lock</c>.</summary>
    public static bool IsHeld(string dataDir)
    {
        var path = RekeySwapJournal.LockPathFor(dataDir);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// The start-up check of a node or an Api, right after <see cref="RekeySwapResolver.Resolve"/>: null when the start
    /// may go on, else the reason it must not.
    /// </summary>
    public static string? StartRefusal(RekeySwapResolution resolution) =>
        IsHeld(resolution.DataDir)
            ? $"A content re-key is running on {resolution.DataDir} ({RekeySwapJournal.LockPathFor(resolution.DataDir)} is held). "
              + "The node starts once it has finished."
            : null;

    private static FileStream? TryOpenExclusive(string path, FileOptions options)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, options);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

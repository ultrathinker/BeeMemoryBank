namespace BeeMemoryBank.Core.IO;

/// <summary>
/// Where a snapshot's working copy of the database is made (review release-a #1): a folder inside the node's data
/// folder, not the OS temp folder. The copy is the whole vault database (key slots, wrapped key, password hashes, titles,
/// paths) until the snapshot filters it, and a kill or power cut leaves it behind. In the OS temp folder that copy stayed
/// forever, wherever the account's TEMP pointed (a shared /tmp, C:\Windows\Temp for a service), and nothing ever removed
/// it. Here it sits next to the live database it is a copy of, owner-only, and the node's next start removes it.
/// </summary>
public static class SnapshotStaging
{
    private const string Prefix = "snapshot-";
    private const string Suffix = ".tmp";

    public static string DirIn(string dataPath) => Path.Combine(dataPath, "tmp");

    /// <summary>
    /// A new, empty, owner-only staging file (0600 on Linux/macOS, an owner-only ACL on Windows) in a 0700 folder.
    /// SQLite's <c>VACUUM INTO</c> accepts an existing empty file and keeps its permissions.
    /// </summary>
    public static string NewFile(string dataPath)
    {
        var dir = DirIn(dataPath);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir, Prefix + Guid.NewGuid().ToString("N") + Suffix);
        OwnerOnlyFile.CreateNew(path).Dispose();
        return path;
    }

    /// <summary>
    /// Removes what an interrupted snapshot left (the staging files and any SQLite sidecar of theirs). Only for a node's
    /// start, when no snapshot of it can be in progress; returns how many files were removed. Files of any other name in
    /// the folder are left alone, and a file that cannot be removed is skipped.
    /// </summary>
    public static int Sweep(string dataPath)
    {
        var dir = DirIn(dataPath);
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, Prefix + "*" + Suffix + "*"))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by another process (a CLI snapshot started a moment before this node), or not ours to remove.
            }
        }
        return removed;
    }
}

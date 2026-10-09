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

    // What a join or a restore works in (review revsrv F10, revios F5): the downloaded archive and the folder it is extracted to. The extracted
    // database is the vault in clear (key slots, password hashes, titles, paths), so these get the same place and rules as the snapshot's copy.
    private const string WorkPrefix = "work-";

    public static string DirIn(string dataPath) => Path.Combine(dataPath, "tmp");

    private static string EnsureDir(string dataPath)
    {
        var dir = DirIn(dataPath);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    /// <summary>
    /// A new, empty, owner-only file for the archive a join downloads or a restore reads, in the staging folder (the data folder, never the OS
    /// temp folder, where it would be world-readable by default and survive a kill for as long as the OS leaves it). Opened afterwards with
    /// <c>File.Create</c>, which keeps the permissions it was made with.
    /// </summary>
    public static string NewArchive(string dataPath)
    {
        var path = Path.Combine(EnsureDir(dataPath), WorkPrefix + Guid.NewGuid().ToString("N") + ".tar.gz");
        OwnerOnlyFile.CreateNew(path).Dispose();
        return path;
    }

    /// <summary>A new, empty, owner-only folder (0700; an owner-only ACL on Windows, inherited by what is extracted into it) in the staging folder.</summary>
    public static string NewDirectory(string dataPath)
    {
        var path = Path.Combine(EnsureDir(dataPath), WorkPrefix + Guid.NewGuid().ToString("N"));
        OwnerOnlyFile.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A new, empty, owner-only staging file (0600 on Linux/macOS, an owner-only ACL on Windows) in a 0700 folder.
    /// SQLite's <c>VACUUM INTO</c> accepts an existing empty file and keeps its permissions.
    /// </summary>
    public static string NewFile(string dataPath)
    {
        var path = Path.Combine(EnsureDir(dataPath), Prefix + Guid.NewGuid().ToString("N") + Suffix);
        OwnerOnlyFile.CreateNew(path).Dispose();
        return path;
    }

    /// <summary>
    /// Removes what an interrupted snapshot, join or restore left (the staging files and any SQLite sidecar of theirs, the archives and the
    /// folders they were extracted to). Only for a node's start, when none of them can be in progress; returns how many entries were removed.
    /// Files and folders of any other name in the staging folder are left alone, and one that cannot be removed is skipped.
    /// </summary>
    public static int Sweep(string dataPath)
    {
        var dir = DirIn(dataPath);
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, Prefix + "*" + Suffix + "*").Concat(Directory.EnumerateFiles(dir, WorkPrefix + "*")).ToList())
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
        foreach (var folder in Directory.EnumerateDirectories(dir, WorkPrefix + "*").ToList())
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // As above.
            }
        }
        return removed;
    }
}

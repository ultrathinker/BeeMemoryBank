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

    /// <summary>The SQLite sidecars of a database file: its rollback journal, its write-ahead log and the log's shared memory.</summary>
    private static readonly string[] SidecarSuffixes = ["-journal", "-wal", "-shm"];

    private static string EnsureDir(string dataPath)
    {
        var dir = DirIn(dataPath);
        // The root is created here and made owner-only every time it is used: SQLite creates the journal and the WAL of a copy beside it, and they
        // take the folder's ACL (on Unix, the copy's mode). An installation from before this rule can have the root with the inherited ACL of the
        // data folder, so an existing root is tightened too. Each working directory below it is owner-only from its creation as well.
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        OwnerOnlyFile.TightenFolder(dir);
        return dir;
    }

    /// <summary>
    /// Removes one tracked working directory after every handle that used it has been closed. A clear SQLite image can
    /// have <c>-wal</c> and <c>-shm</c> siblings, so this deliberately removes every file in the directory rather than
    /// only the database's expected name. A sharing violation is retried: returning false means the caller must fail
    /// closed instead of reporting that the operation which made the clear image succeeded.
    /// </summary>
    public static bool TryCleanupDirectory(string directory, Func<string, bool>? deleteFile = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!Directory.Exists(directory)) return true;
            try
            {
                // Delete files first so SQLite sidecars and a non-empty media directory cannot make the parent delete
                // look successful while retaining the clear database under it.
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    if (deleteFile is not null)
                    {
                        if (!deleteFile(file)) throw new IOException($"Could not remove staging file {file}.");
                    }
                    else
                    {
                        File.Delete(file);
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                             .OrderByDescending(p => p.Length))
                    Directory.Delete(child, recursive: false);
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 2) Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                continue;
            }

            if (!Directory.Exists(directory)) return true;
        }
        return !Directory.Exists(directory);
    }

    /// <summary>
    /// Removes a staging database file and each SQLite sidecar of it (<see cref="SidecarSuffixes"/>): one rule for every caller, a copy and its
    /// sidecars are one entry. Each name still there goes through <paramref name="deleteFile"/> (a caller that wipes passes a wipe for all of them);
    /// the whole set is tried again, three times in all, until none is left. True only when no name of the copy exists: a caller that gets false
    /// reports repair-required and never a success.
    /// </summary>
    /// <param name="deleteFile">Removes one file (its result is not needed: whether a name is still there is checked after each attempt). Null:
    /// <see cref="File.Delete(string)"/>.</param>
    public static bool TryCleanupFile(string path, Func<string, bool>? deleteFile = null)
    {
        var names = NamesOf(path).ToArray();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var name in names.Where(Exists))
            {
                try
                {
                    if (deleteFile is null) File.Delete(name);
                    else deleteFile(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still there: the next attempt tries it again, and the last one reports it.
                }
            }
            if (!names.Any(Exists)) return true;
            if (attempt < 2) Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
        }
        return false;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>A staging copy and each of its SQLite sidecars.</summary>
    private static IEnumerable<string> NamesOf(string copy) => [copy, .. SidecarSuffixes.Select(suffix => copy + suffix)];

    /// <summary>The staging copy a file belongs to: the file's own name, or its name without a sidecar suffix.</summary>
    private static string CopyName(string file)
    {
        foreach (var suffix in SidecarSuffixes)
            if (file.EndsWith(suffix, StringComparison.Ordinal)) return file[..^suffix.Length];
        return file;
    }

    /// <summary>True only when no tracked archive, copy or extracted working directory remains in the staging root.</summary>
    public static bool IsEmpty(string dataPath)
    {
        var dir = DirIn(dataPath);
        return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
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
        // A sidecar whose copy is gone is swept under the copy's name; each file that was there counts, as it always did.
        foreach (var copy in Directory.EnumerateFiles(dir, Prefix + "*" + Suffix + "*").Select(CopyName).Distinct().ToList())
        {
            var present = NamesOf(copy).Count(Exists);
            if (TryCleanupFile(copy)) removed += present;
        }
        foreach (var archive in Directory.EnumerateFiles(dir, WorkPrefix + "*").ToList())
        {
            if (TryCleanupFile(archive)) removed++;
        }
        foreach (var folder in Directory.EnumerateDirectories(dir, WorkPrefix + "*").ToList())
        {
            if (TryCleanupDirectory(folder)) removed++;
        }
        return removed;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// The swap journal of an offline re-key (rekey-offline.md §2 step 6): <c>&lt;D&gt;.rekey-journal.json</c>, next to
/// the data directory D. It names the new vault (<c>D.rekey-new</c>), the place the old one goes
/// (<c>D.pre-rekey-&lt;ts&gt;</c>) and how far the swap got. It is written durably: a temp file, flushed to disk, then
/// renamed over the journal.
/// </summary>
public sealed record RekeySwapJournal(
    [property: JsonPropertyName("new")] string New,
    [property: JsonPropertyName("old")] string Old,
    [property: JsonPropertyName("phase")] string Phase)
{
    /// <summary>Written before the first rename: the new vault is verified and carried over, D is still the old one.</summary>
    public const string Prepared = "prepared";
    /// <summary>After rename 1: D is at <see cref="Old"/>.</summary>
    public const string OldMoved = "old-moved";
    /// <summary>After rename 2: the new vault is D; its first start has not succeeded yet.</summary>
    public const string Swapped = "swapped";

    public static string PathFor(string dataDir) => Normalize(dataDir) + ".rekey-journal.json";

    /// <summary>The re-key lock (L's): a normal start refuses while it exists, except for the first start after a swap.</summary>
    public static string LockPathFor(string dataDir) => Normalize(dataDir) + ".rekey.lock";

    public static string NewDirFor(string dataDir) => Normalize(dataDir) + ".rekey-new";

    public static string OldDirFor(string dataDir, DateTimeOffset at) =>
        Normalize(dataDir) + ".pre-rekey-" + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

    internal static string Normalize(string dataDir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));

    public static RekeySwapJournal? Read(string dataDir)
    {
        var path = PathFor(dataDir);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RekeySwapJournal>(File.ReadAllText(path))
               ?? throw new InvalidDataException($"The re-key journal {path} is empty.");
    }

    /// <summary>Temp file, flushed to disk, atomically renamed over the journal.</summary>
    public static void Write(string dataDir, RekeySwapJournal journal)
    {
        var path = PathFor(dataDir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, journal);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string dataDir)
    {
        File.Delete(PathFor(dataDir));
        File.Delete(PathFor(dataDir) + ".tmp");
    }
}

/// <param name="DataDir">The data directory to start on: D, holding the old or the new vault.</param>
/// <param name="FirstStartAfterSwap">D is a re-keyed vault that has not started yet: the re-key lock is expected, and
/// <see cref="RekeySwapResolver.CompleteFirstStart"/> removes it with the journal once the start succeeded.</param>
public sealed record RekeySwapResolution(string DataDir, bool FirstStartAfterSwap);

/// <summary>
/// Called by every process that resolves the data directory, before anything creates or opens it: finishes a swap a
/// crash interrupted, or rolls it back, so D always holds a startable vault — the old one or the new one, never
/// neither. With no journal it does nothing. The decision is taken from what is on disk, the journal's phase only
/// names the directories: a crash can fall between a rename and the journal update after it.
/// <list type="bullet">
/// <item>D and the new vault, no old dir: rename 1 has not happened. The journal is only written once the new vault
///   is verified, so the swap goes forward.</item>
/// <item>No D, the old dir and the new vault: between the renames. Rename 2 is done now.</item>
/// <item>D and the old dir, no new vault: both renames are done.</item>
/// <item>The new vault is gone: the swap cannot finish, so the old vault goes back to D and the journal is removed.</item>
/// </list>
/// </summary>
public static class RekeySwapResolver
{
    public static RekeySwapResolution Resolve(string dataDir)
    {
        var d = RekeySwapJournal.Normalize(dataDir);
        var journal = RekeySwapJournal.Read(d);
        if (journal == null) return new(d, false);

        var dExists = Directory.Exists(d);
        var oldExists = Directory.Exists(journal.Old);
        var newExists = Directory.Exists(journal.New);

        // A D that something created empty while the vault was at the old dir (an older start path) holds nothing.
        if (dExists && newExists && oldExists && IsEmpty(d))
        {
            Directory.Delete(d);
            dExists = false;
        }

        if (dExists && !oldExists)
        {
            if (!newExists)
            {
                RekeySwapJournal.Delete(d); // nothing to swap to: the old vault stays
                return new(d, false);
            }
            RenameWithRetry(d, journal.Old);
            RekeySwapJournal.Write(d, journal with { Phase = RekeySwapJournal.OldMoved });
            dExists = false;
            oldExists = true;
        }

        if (!dExists)
        {
            if (newExists)
            {
                RenameWithRetry(journal.New, d);
                RekeySwapJournal.Write(d, journal with { Phase = RekeySwapJournal.Swapped });
                return new(d, true);
            }
            if (oldExists)
            {
                RenameWithRetry(journal.Old, d); // the new vault is gone: back to the old one
                RekeySwapJournal.Delete(d);
                return new(d, false);
            }
            throw new InvalidOperationException(
                $"The re-key journal {RekeySwapJournal.PathFor(d)} names neither vault: {journal.New} and {journal.Old} are both missing, and so is {d}.");
        }

        // D and the old dir: both renames are done.
        if (journal.Phase != RekeySwapJournal.Swapped)
            RekeySwapJournal.Write(d, journal with { Phase = RekeySwapJournal.Swapped });
        return new(d, true);
    }

    /// <summary>After the first start on a swapped-in vault succeeded: the journal, then the re-key lock, are removed.
    /// A no-op otherwise.</summary>
    public static void CompleteFirstStart(string dataDir)
    {
        var d = RekeySwapJournal.Normalize(dataDir);
        var journal = RekeySwapJournal.Read(d);
        if (journal is not { Phase: RekeySwapJournal.Swapped } || !Directory.Exists(d) || Directory.Exists(journal.New)) return;
        RekeySwapJournal.Delete(d);
        File.Delete(RekeySwapJournal.LockPathFor(d));
    }

    private static bool IsEmpty(string dir) => !Directory.EnumerateFileSystemEntries(dir).Any();

    /// <summary>
    /// Directory.Move, retried briefly: on Windows a handle still closing inside the directory (an antivirus scan, a
    /// pooled SQLite connection being released) fails the rename for a moment. A persistent failure throws, and the
    /// next start takes the decision again from what is on disk.
    /// </summary>
    public static void RenameWithRetry(string from, string to, int attempts = 10)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts)
            {
                Thread.Sleep(200 * i);
            }
        }
    }
}

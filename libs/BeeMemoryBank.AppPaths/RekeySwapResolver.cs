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
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The re-key journal {path} is a link, not a file the swap wrote.");
        return JsonSerializer.Deserialize<RekeySwapJournal>(File.ReadAllText(path))
               ?? throw new InvalidDataException($"The re-key journal {path} is empty.");
    }

    /// <summary>
    /// Temp file, flushed to disk, atomically renamed over the journal, and the rename made durable (the parent
    /// directory flushed, write-through on Windows): a flushed file whose directory entry is lost is no journal at all
    /// (review release-b R1-5).
    /// </summary>
    public static void Write(string dataDir, RekeySwapJournal journal)
    {
        var path = PathFor(dataDir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, journal);
            fs.Flush(flushToDisk: true);
        }
        DurableFs.MoveFile(tmp, path);
    }

    public static void Delete(string dataDir)
    {
        File.Delete(PathFor(dataDir));
        File.Delete(PathFor(dataDir) + ".tmp");
        DurableFs.FlushDirectory(Path.GetDirectoryName(PathFor(dataDir))!);
    }

    /// <summary>The report the verb writes into the new vault once it is verified, before the swap.</summary>
    public const string VerifiedMarker = "rekey-report.json";

    /// <summary>The old vaults a swap parked next to D, newest first.</summary>
    public static IReadOnlyList<string> ParkedOldDirs(string dataDir)
    {
        var d = Normalize(dataDir);
        var parent = Path.GetDirectoryName(d)!;
        if (!Directory.Exists(parent)) return [];
        return Directory.EnumerateDirectories(parent, Path.GetFileName(d) + ".pre-rekey-*")
            .OrderByDescending(p => p, StringComparer.Ordinal).ToList();
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
        RekeySwapJournal? read;
        try
        {
            read = RekeySwapJournal.Read(d);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            // A journal that does not parse tells nothing: it is moved aside (kept, for whoever looks; left in place it
            // would stop every later re-key) and the siblings decide, as for a lost journal (R1-6).
            File.Move(RekeySwapJournal.PathFor(d), RekeySwapJournal.PathFor(d) + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"), overwrite: true);
            return ResolveWithoutJournal(d);
        }
        if (read == null) return ResolveWithoutJournal(d);
        var journal = Trusted(d, read);

        var dExists = Directory.Exists(d);
        var oldExists = IsRealDirectory(journal.Old);
        var newExists = IsRealDirectory(journal.New);

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
        RekeySwapJournal? journal;
        try { journal = RekeySwapJournal.Read(d); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException) { return; } // Resolve decides such a journal
        if (journal is not { Phase: RekeySwapJournal.Swapped } || !Directory.Exists(d) || Directory.Exists(RekeySwapJournal.NewDirFor(d))) return;
        RekeySwapJournal.Delete(d);
        File.Delete(RekeySwapJournal.LockPathFor(d));
    }

    /// <summary>
    /// No journal. That is the normal case, and nothing is done while D exists. With D missing, the swap may have been
    /// under way when a power cut lost the journal's directory entry (review release-b R1-5), so the siblings decide.
    /// A verified new vault (the verb writes its report into it before the swap) goes to D. Failing that, the newest
    /// parked old vault goes back. With neither, this is a first run and D is created as usual.
    /// </summary>
    private static RekeySwapResolution ResolveWithoutJournal(string d)
    {
        if (Directory.Exists(d)) return new(d, false);
        var newDir = RekeySwapJournal.NewDirFor(d);
        var parked = RekeySwapJournal.ParkedOldDirs(d).Where(IsRealDirectory).ToList();
        if (IsRealDirectory(newDir) && File.Exists(Path.Combine(newDir, RekeySwapJournal.VerifiedMarker)))
        {
            RenameWithRetry(newDir, d);
            // Recorded as swapped, so the first start clears it (and the re-key lock) like any other swap.
            RekeySwapJournal.Write(d, new RekeySwapJournal(newDir, parked.FirstOrDefault() ?? RekeySwapJournal.OldDirFor(d, DateTimeOffset.UtcNow),
                RekeySwapJournal.Swapped));
            return new(d, true);
        }
        if (parked.Count > 0)
        {
            RenameWithRetry(parked[0], d);
            return new(d, false);
        }
        return new(d, false);
    }

    private static bool IsEmpty(string dir) => !Directory.EnumerateFileSystemEntries(dir).Any();

    /// <summary>
    /// The journal is untrusted state (review release-b R1-6): it names the directories, but nothing it names is moved
    /// unless it is exactly what the swap itself would have created next to D.
    /// <list type="bullet">
    /// <item><b>new</b> is always <c>D.rekey-new</c>, derived from D; the journal's value is ignored.</item>
    /// <item><b>old</b> is kept only if it is a sibling of D named <c>D.pre-rekey-yyyyMMddTHHmmssZ</c>. Otherwise it is
    ///   the newest such sibling that exists, or a fresh name for a swap that has not moved D yet.</item>
    /// <item>An unknown phase is read as <c>prepared</c>: the decision is taken from the disk anyway.</item>
    /// </list>
    /// A directory that is a junction or a symbolic link is never treated as either vault (<see cref="IsRealDirectory"/>).
    /// </summary>
    private static RekeySwapJournal Trusted(string d, RekeySwapJournal journal)
    {
        var newDir = RekeySwapJournal.NewDirFor(d);
        var old = IsParkedOldName(d, journal.Old)
            ? RekeySwapJournal.Normalize(journal.Old!)
            : RekeySwapJournal.ParkedOldDirs(d).FirstOrDefault(IsRealDirectory) ?? RekeySwapJournal.OldDirFor(d, DateTimeOffset.UtcNow);
        var phase = journal.Phase is RekeySwapJournal.Prepared or RekeySwapJournal.OldMoved or RekeySwapJournal.Swapped
            ? journal.Phase
            : RekeySwapJournal.Prepared;
        return new RekeySwapJournal(newDir, old, phase);
    }

    private static readonly System.Text.RegularExpressions.Regex ParkedSuffix =
        new(@"^\.pre-rekey-\d{8}T\d{6}Z$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsParkedOldName(string d, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        string full;
        try { full = RekeySwapJournal.Normalize(candidate); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var name = Path.GetFileName(d);
        return string.Equals(Path.GetDirectoryName(full), Path.GetDirectoryName(d), PathComparison.ForCurrentPlatform())
               && Path.GetFileName(full).StartsWith(name, StringComparison.Ordinal)
               && ParkedSuffix.IsMatch(Path.GetFileName(full)[name.Length..]);
    }

    /// <summary>A directory that exists and is not a junction or symbolic link: only such a directory is ever a vault here.</summary>
    public static bool IsRealDirectory(string? path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path)
        && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) == 0;

    /// <summary>
    /// A durable directory rename (<see cref="DurableFs.MoveDirectory"/>), retried briefly: on Windows a handle still
    /// closing inside the directory (an antivirus scan, a pooled SQLite connection being released) fails the rename for
    /// a moment. A persistent failure throws, and the next start takes the decision again from what is on disk.
    /// </summary>
    public static void RenameWithRetry(string from, string to, int attempts = 10)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                DurableFs.MoveDirectory(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts)
            {
                Thread.Sleep(200 * i);
            }
        }
    }
}

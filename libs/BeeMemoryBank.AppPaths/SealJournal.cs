using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// The journal of "Encrypt this node" (docs/design/step-b-spec.md 10.3): <c>&lt;data&gt;/seal-journal.json</c>, written like the re-key's
/// (<see cref="RekeySwapJournal.Write"/>): a temp file, flushed to disk, renamed over the journal, the directory flushed. It holds the phase and
/// the names of the files the migration works on; no key, no name of a note, no path outside the data folder.
/// </summary>
/// <param name="Phase"><see cref="Building"/>, <see cref="Verified"/> or <see cref="Swapping"/>.</param>
/// <param name="StartedAt">UTC, ISO 8601.</param>
/// <param name="Files">The relative names of the files being replaced (or, in a journal the resolver rebuilt after a failed wipe, of the leftovers still to be wiped).</param>
/// <param name="Snapshot">Relative name of the pre-encryption snapshot (<c>pre-encryption/&lt;file&gt;</c>), or empty (a phone, or the owner ticked <c>snapshot_limit</c>).</param>
public sealed record SealJournal(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("snapshot")] string Snapshot)
{
    /// <summary>The targets are being made; the live files were never touched.</summary>
    public const string Building = "building";
    /// <summary>The targets are verified and flushed; the swap may start (and, after a crash, go forward).</summary>
    public const string Verified = "verified";
    /// <summary>The swap is under way or done; what is left is the wipe of the replaced plain files.</summary>
    public const string Swapping = "swapping";

    public const string FileName = "seal-journal.json";
    public const string UnreadableSuffix = ".unreadable";

    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    public static string UnreadablePathFor(string dataDir) => PathFor(dataDir) + UnreadableSuffix;

    public static bool IsKnownPhase(string? phase) => phase is Building or Verified or Swapping;

    /// <summary>
    /// What the journal file is. A journal that is not there is <see cref="SealJournalState.Missing"/>; one that does not parse or does not
    /// have the shape of this record (an unknown phase, a name that leaves the data folder) is <see cref="SealJournalState.Unreadable"/> and is
    /// never trusted. Reading changes nothing on disk.
    /// </summary>
    public static (SealJournalState State, SealJournal? Journal) Read(string dataDir)
    {
        var path = PathFor(dataDir);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (SealJournalState.Missing, null);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null) return (SealJournalState.Unreadable, null);
            if (info.Length > 64 * 1024) return (SealJournalState.Unreadable, null);
            var journal = JsonSerializer.Deserialize<SealJournal>(File.ReadAllText(path));
            return IsWellFormed(journal) ? (SealJournalState.Valid, journal) : (SealJournalState.Unreadable, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (SealJournalState.Unreadable, null);
        }
    }

    private static bool IsWellFormed(SealJournal? j)
    {
        if (j is null || !IsKnownPhase(j.Phase) || j.StartedAt is null || j.Files is null || j.Snapshot is null) return false;
        if (j.Files.Any(f => !IsPlainRelativeName(f))) return false;
        return j.Snapshot.Length == 0 || (j.Snapshot.StartsWith("pre-encryption/", StringComparison.Ordinal) && IsPlainRelativeName(j.Snapshot["pre-encryption/".Length..]));
    }

    /// <summary>A name inside the data folder: not empty, no separator, no <c>..</c>, no drive.</summary>
    private static bool IsPlainRelativeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && name is not ("." or "..");

    /// <summary>Writes the journal durably (temp file, flush, rename, directory flush).</summary>
    public static void Write(string dataDir, SealJournal journal)
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

    /// <summary>Removes the journal and a temp file of it; the directory is flushed. A journal that cannot be removed is reported with false.</summary>
    public static bool Delete(string dataDir)
    {
        try
        {
            File.Delete(PathFor(dataDir));
            File.Delete(PathFor(dataDir) + ".tmp");
            DurableFs.FlushDirectory(dataDir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SealJournal New(string phase, IReadOnlyList<string> files, string snapshot, TimeProvider? time = null) =>
        new(phase, (time ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture), files, snapshot);
}

public enum SealJournalState
{
    Missing,
    Valid,
    Unreadable,
}

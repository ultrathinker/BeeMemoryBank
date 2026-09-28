using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The file side of a blind seed's cutover (review L-stage1 #4): the new database and every media file
/// are staged and checked first, a durable marker says how far the switch got, and the old database is
/// kept until the whole cutover is done — so a process that dies anywhere in between is either rolled
/// back to the old database or finished at the next start (<see cref="Recover"/>), never left on a new
/// database without its media or its replayed events.
///
/// <para>A marker that cannot be read at all is the one state the files alone must decide: the old
/// database and media in the directory mean a switch had begun, so the cutover is
/// <see cref="Unresolved"/> — kept, and refused as a base for a new seed — rather than deleted as
/// one that never started (Codex #3).</para>
///
/// <para>Layout, in the data volume (not in blind-tmp, which every start empties):
/// <c>blind-cutover/marker.json</c> (seed id, phase), <c>new.db</c>, <c>media/</c> (the complete new
/// media directory), and, once the switch has begun, <c>old.db</c> (+ -wal/-shm) and <c>old-media/</c>.
/// The media directory is switched whole, by renames on the same volume, so there is never a
/// half-moved set of files: the old one is intact beside the new one until the cutover is done.</para>
/// </summary>
public sealed class BlindSeedCutover
{
    public const string Staged = "staged";
    public const string Switching = "switching";
    public const string Done = "done";

    /// <summary>
    /// Not a phase the cutover writes: the marker is missing or cannot be read while the directory
    /// still holds what only a switch creates. Read as "never started", that state cost the old
    /// database and media — the only way back (Codex #3).
    /// </summary>
    public const string Unresolved = "unresolved";

    private static readonly string[] Sidecars = ["-wal", "-shm"];

    private readonly string _dir;
    private readonly string _livePath;
    private readonly string _liveMedia;

    public BlindSeedCutover(string dataPath)
    {
        _dir = DirOf(dataPath);
        _livePath = LivePathOf(dataPath);
        _liveMedia = Path.Combine(dataPath, "media");
    }

    public static string DirOf(string dataPath) => Path.Combine(dataPath, "blind-cutover");
    private static string LivePathOf(string dataPath) => Path.Combine(dataPath, "beememorybank.db");

    public string StagedDbPath => Path.Combine(_dir, "new.db");
    private string StagedMediaDir => Path.Combine(_dir, "media");
    private string OldDbPath => Path.Combine(_dir, "old.db");
    private string OldMediaDir => Path.Combine(_dir, "old-media");
    private string MarkerPath => Path.Combine(_dir, "marker.json");

    /// <summary>
    /// Starts from nothing — except that an unresolved switch is never thrown away: its old.db and
    /// old-media are the only way back, so a new seed is refused until a start has rolled it back.
    /// A cutover that got to "done" is finished first; a staged one that never switched is dropped.
    /// </summary>
    public void Prepare()
    {
        if (Directory.Exists(_dir))
        {
            switch (PhaseOf().Phase)
            {
                case Switching:
                    throw new BlindSeedRejectedException(
                        "An earlier seed's switch was interrupted and is not resolved; this node rolls it back at its next start. Restart it, then send the seed again.");
                case Unresolved:
                    throw new BlindSeedRejectedException(
                        "An earlier seed's cutover is unresolved: its marker is missing or unreadable and the database it replaced is still kept in blind-cutover. Nothing was discarded. Restart this node, which keeps or restores what is there; resolve blind-cutover by hand if it stays unresolved. No seed is accepted until then.");
                case Done:
                    FinishDone();
                    break;
                default:
                    Directory.Delete(_dir, recursive: true);
                    break;
            }
        }
        Directory.CreateDirectory(StagedMediaDir);
    }

    /// <summary>
    /// The complete new media directory: the package's media, each copy checked against its source,
    /// plus whatever the live directory holds besides media files.
    /// </summary>
    public void StageMedia(string packageMediaDir)
    {
        if (Directory.Exists(_liveMedia))
            foreach (var other in Directory.GetFiles(_liveMedia).Where(f => !f.EndsWith(".enc", StringComparison.Ordinal)))
                File.Copy(other, Path.Combine(StagedMediaDir, Path.GetFileName(other)), overwrite: true);
        if (!Directory.Exists(packageMediaDir)) return;
        foreach (var source in Directory.GetFiles(packageMediaDir, "*.enc"))
        {
            var staged = Path.Combine(StagedMediaDir, Path.GetFileName(source));
            File.Copy(source, staged, overwrite: true);
            if (!HashOf(staged).AsSpan().SequenceEqual(HashOf(source)))
                throw new IOException($"Staged media {Path.GetFileName(source)} does not match the package.");
        }
    }

    /// <summary>
    /// Durably records the phase. A "switching" marker also records whether a live media directory
    /// existed before the switch: without one, the directory a rollback finds live is the seed's, and
    /// it must go rather than stay beside the old database.
    /// </summary>
    public void WriteMarker(Guid seedId, string phase)
    {
        var temp = MarkerPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(file, new Marker(seedId, phase, phase == Switching ? Directory.Exists(_liveMedia) : null));
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, MarkerPath, overwrite: true);
    }

    /// <summary>
    /// The switch itself: live database and live media directory aside (kept), staged ones in. Pools
    /// must be cleared around it. Each step is a rename; <see cref="RollBack"/> undoes any prefix of them.
    ///
    /// <para>Every move checks where it stands first — the main file and each sidecar on its own —
    /// because the caller's retry wrapper repeats the whole action after an IOException, which can come
    /// between any two of them. Repeated blindly, the first step would move the NEW database over old.db
    /// (the only copy of the old one), and a -wal left behind by a failed move holds committed frames
    /// of the old database: until the new database is in, a sidecar beside the vacant live path is
    /// moved after its main file, never deleted. The only one that is deleted is a sidecar whose
    /// counterpart already moved: it came from a request allowed through maintenance (authenticate)
    /// that opened the vacant path and so created an empty database there, which the new database
    /// replaces.</para>
    /// </summary>
    public void SwitchFiles()
    {
        if (File.Exists(StagedDbPath))
        {
            if (!File.Exists(OldDbPath)) File.Move(_livePath, OldDbPath);
            foreach (var suffix in Sidecars)
            {
                if (!File.Exists(_livePath + suffix)) continue;
                if (File.Exists(OldDbPath + suffix)) File.Delete(_livePath + suffix);
                else File.Move(_livePath + suffix, OldDbPath + suffix);
            }
            File.Move(StagedDbPath, _livePath, overwrite: true);
        }
        if (Directory.Exists(_liveMedia) && !Directory.Exists(OldMediaDir)) Directory.Move(_liveMedia, OldMediaDir);
        if (Directory.Exists(StagedMediaDir)) Directory.Move(StagedMediaDir, _liveMedia);
    }

    /// <summary>
    /// Back to the database and media directory the cutover started from, whichever of the switch's
    /// steps had happened; the new ones are discarded. Resumable like <see cref="SwitchFiles"/>: the old
    /// main file comes back first, then each old sidecar still in the cutover directory, and the
    /// directory goes only once nothing of the old database is left in it.
    /// </summary>
    public void RollBack()
    {
        var newDbInstalled = !File.Exists(StagedDbPath);
        if (File.Exists(OldDbPath))
        {
            // What is live is not the old database: the new one, or an empty one a stray open created at
            // the vacant path. Its sidecars go with it — except before the new database was moved in,
            // when a sidecar still beside the live path is the old database's own, never moved.
            if (File.Exists(_livePath)) File.Delete(_livePath);
            if (newDbInstalled)
                foreach (var suffix in Sidecars) if (File.Exists(_livePath + suffix)) File.Delete(_livePath + suffix);
            File.Move(OldDbPath, _livePath);
        }
        foreach (var suffix in Sidecars)
            if (File.Exists(OldDbPath + suffix)) File.Move(OldDbPath + suffix, _livePath + suffix, overwrite: true);

        if (Directory.Exists(OldMediaDir))
        {
            if (Directory.Exists(_liveMedia)) Directory.Delete(_liveMedia, recursive: true);
            Directory.Move(OldMediaDir, _liveMedia);
        }
        else if (ReadMarker()?.LiveMediaExisted == false && Directory.Exists(_liveMedia))
        {
            // (Only ever reached from a marker that says "switching": with one that cannot be read at
            // all there is no rollback to get here through — see RecoverUnresolved.)
            // There was no media directory before the switch: the live one is the seed's.
            Directory.Delete(_liveMedia, recursive: true);
        }
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The cutover is durable from here on: a start finishes it, it is never rolled back.</summary>
    public void MarkDone(Guid seedId) => WriteMarker(seedId, Done);

    /// <summary>
    /// After "done" (the new seed is committed and the node runs on it): the old database and old media are
    /// no longer a way back, only a copy of the vault as it was, under whatever key it was under then. After
    /// a content re-key that is the whole vault under the replaced key, and in any case it holds the
    /// recovery material a purge removed; it used to stay as <c>beememorybank.db.pre-seed</c> and go into
    /// every copy of the data volume. It is now wiped: each database file (main and sidecars) is
    /// overwritten with zeros in place, then deleted; the old media files are deleted. Nothing ever read the
    /// pre-seed copy. A <c>.pre-seed</c> left by an older build goes the same way. Resumable: every file is
    /// wiped where it is, and the directory goes last.
    /// </summary>
    public void FinishDone()
    {
        foreach (var file in new[] { OldDbPath }.Concat(Sidecars.Select(x => OldDbPath + x)))
            WipeFile(file);
        WipePreSeedLeftovers(_livePath);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The rollback copy older builds kept after a completed seed: <c>beememorybank.db.pre-seed</c> and its sidecars.</summary>
    private static IEnumerable<string> PreSeedFiles(string livePath) =>
        new[] { livePath + ".pre-seed" }.Concat(Sidecars.Select(x => livePath + ".pre-seed" + x));

    private static void WipePreSeedLeftovers(string livePath)
    {
        foreach (var file in PreSeedFiles(livePath)) WipeFile(file);
    }

    /// <summary>
    /// Zeros over the whole file, flushed, then the file deleted: no page of it stays readable through the
    /// file system (flash storage may still hold the blocks; that is not promised). A missing file is fine.
    /// </summary>
    internal static void WipeFile(string path)
    {
        if (!File.Exists(path)) return;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var zeros = new byte[64 * 1024];
            var remaining = file.Length;
            while (remaining > 0)
            {
                var n = (int)Math.Min(zeros.Length, remaining);
                file.Write(zeros, 0, n);
                remaining -= n;
            }
            file.Flush(flushToDisk: true);
        }
        File.Delete(path);
    }

    /// <summary>
    /// The phase of the cutover directory, judged from the marker <b>and</b> from what is in it. A
    /// marker that cannot be read is not an absent one: the old database and media beside it are the
    /// only way back, and only a directory holding none of them can be dropped as never started
    /// (Codex #3). <see cref="SwitchFiles"/> moves the old database aside first and the old media
    /// second, so either one being there means the switch had begun.
    /// </summary>
    private (string Phase, Guid? SeedId) PhaseOf()
    {
        if (ReadMarker() is { } marker) return (marker.Phase, marker.SeedId);
        if (File.Exists(OldDbPath) || Directory.Exists(OldMediaDir)) return (Unresolved, null);
        return (Staged, null);
    }

    private Marker? ReadMarker()
    {
        if (!File.Exists(MarkerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A marker is replaced atomically, so one that cannot be read is torn or damaged — the
            // state of a process that died inside the switch, which is exactly when the answer
            // matters. Reported as unreadable (null here, plus the files, in PhaseOf), never as
            // absent.
            return null;
        }
    }

    /// <summary>
    /// At start, before the database is opened: finishes a cutover that got to "done", rolls back one
    /// that was switching, keeps — and repairs — one whose marker cannot be read, and drops one that
    /// never started switching.
    /// </summary>
    public static void Recover(string dataPath, ILogger logger)
    {
        var cutover = new BlindSeedCutover(dataPath);
        if (!Directory.Exists(cutover._dir))
        {
            // No cutover in progress: a rollback copy left by an older build is nobody's way back.
            if (PreSeedFiles(cutover._livePath).Any(File.Exists))
            {
                WipePreSeedLeftovers(cutover._livePath);
                logger.LogInformation("Blind seed: wiped the pre-seed copy an older build kept after a completed seed");
            }
            return;
        }
        SqliteConnection.ClearAllPools();

        var (phase, seedId) = cutover.PhaseOf();
        switch (phase)
        {
            case Done:
                cutover.FinishDone();
                logger.LogWarning("Blind seed {SeedId}: finished a cutover interrupted after it was complete", seedId);
                break;
            case Switching:
                cutover.RollBack();
                logger.LogWarning("Blind seed {SeedId}: rolled back a cutover interrupted mid-switch; the seed can be sent again", seedId);
                break;
            case Unresolved:
                cutover.RecoverUnresolved(logger);
                break;
            default:
                Directory.Delete(cutover._dir, recursive: true);
                logger.LogInformation("Blind seed: dropped a cutover that never started switching");
                break;
        }
    }

    /// <summary>
    /// A cutover directory whose marker is missing or unreadable while the database (or the media
    /// directory) it replaced is still in it: the switch may have happened, and nothing here can say
    /// whether it finished — so nothing is discarded (Codex #3). The old files are put back where the
    /// node cannot start without them (a live database or live media directory that is gone), and
    /// otherwise left exactly where they are for an operator, with the state spelled out; the next
    /// seed is refused until it is resolved.
    /// </summary>
    private void RecoverUnresolved(ILogger logger)
    {
        var restoredDb = false;
        if (!File.Exists(_livePath) && File.Exists(OldDbPath))
        {
            // Nothing live to lose, and a node without its database does not start: the old one,
            // sidecars first, goes back as the live one.
            foreach (var suffix in Sidecars)
                if (File.Exists(OldDbPath + suffix)) File.Move(OldDbPath + suffix, _livePath + suffix, overwrite: true);
            File.Move(OldDbPath, _livePath);
            restoredDb = true;
        }

        var restoredMedia = false;
        if (!Directory.Exists(_liveMedia) && Directory.Exists(OldMediaDir))
        {
            Directory.Move(OldMediaDir, _liveMedia);
            restoredMedia = true;
        }

        logger.LogError(
            "Blind seed: the cutover marker in {Dir} is missing or unreadable, and the database this node replaced is still there{Db}{Media}. Nothing was discarded; no new seed is accepted until this is resolved. Restart after checking blind-cutover, or restore the marker.",
            _dir,
            restoredDb ? " — it has been put back as the live database, there being none" : "",
            restoredMedia ? " — the old media directory has been put back, there being none" : "");
    }

    private static byte[] HashOf(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }

    private sealed record Marker(Guid SeedId, string Phase, bool? LiveMediaExisted = null);
}

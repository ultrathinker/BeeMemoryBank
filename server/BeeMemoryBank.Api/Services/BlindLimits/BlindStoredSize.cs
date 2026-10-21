namespace BeeMemoryBank.Api.Services.BlindLimits;

/// <summary>One measurement of the node's stored bytes.</summary>
/// <param name="Known">False when the size could not be measured: unknown is never shown as zero.</param>
/// <param name="Bytes">The bytes the node holds for the mesh.</param>
public sealed record BlindStoredReading(bool Known, long Bytes)
{
    public static readonly BlindStoredReading Unknown = new(false, 0);
}

/// <summary>
/// How many bytes this node holds for the mesh — what a storage limit is judged against: the vault
/// database (the blob table's ciphertext is inside the file, so it is counted once; the WAL holds
/// real rows between checkpoints), the encrypted media files, the sealed baselines, and what the node
/// writes into for a peer while it works: <c>blind-tmp</c> (a seed that is arriving in parts and the
/// package it is unpacked into, the replica packages built for phones) and <c>blind-cutover</c> (the
/// new database and media of a seed being switched in, and the old ones until the switch is done).
/// Real bytes on the disk: a file SQLite has not shrunk after deletes still takes its room.
///
/// <para>What a seed that is being received or applied stages is the seed's own affair: it judges itself with exact numbers, part by part, and registers the folders it works in
/// (<see cref="Exclude"/>) for as long as it works, so that the walk the OTHER callers' decisions rest on does not see the peak of an apply (the unpacked package, the new
/// database) and pause every phone for an hour right after a successful seed. The walk ends the registration with an <see cref="Invalidate"/>: the next decision measures again.</para>
///
/// <para>The node's own backup staging (<c>blind/stage</c>) and its restic cache are not counted: they
/// are the host's own copy of the node, not something a peer can make the node write.</para>
///
/// <para>Measured by a walk, cached for a minute (a directory enumeration, cheap but not
/// per-request) and refreshed by <see cref="EnsureFresh"/> before a storage limit decides. What a
/// write moved after the last walk is added to the reading until the next walk
/// (<see cref="Release"/>), and what the writes in flight may still add is reserved at admission
/// (<see cref="Judge"/> with a reservation), so a minute of requests cannot all be judged against one old number. A file
/// that cannot be looked at mid-walk is skipped, as the status walk does; a database file that
/// cannot be looked at makes the reading <see cref="BlindStoredReading.Unknown"/>, and the guard
/// pauses a limited node on it (fail closed: unknown is never "there is room").</para>
/// </summary>
public sealed class BlindStoredSize(string dataPath, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private BlindStoredReading _cached = new(true, 0);
    private DateTimeOffset _measuredAt;
    private bool _fresh;
    private long _written;
    private long _reserved;
    private readonly List<string> _excluded = [];

    /// <summary>The last measurement and what was written since it, without walking.</summary>
    public BlindStoredReading Peek()
    {
        lock (_gate) return Current();
    }

    private BlindStoredReading Current() => _cached.Known ? new BlindStoredReading(true, Saturating(_cached.Bytes, _written)) : _cached;

    /// <summary>
    /// Judges a write of <paramref name="bytes"/> against the <paramref name="cap"/> and, with <paramref name="reserve"/>, reserves them when they fit - one locked step.
    /// <see cref="BlindRoom.Reached"/>: what the node holds leaves no room (or it cannot be measured: <paramref name="known"/> says which); <see cref="BlindRoom.Busy"/>: it would
    /// fit, but the writes in flight have reserved the room.
    /// </summary>
    public BlindRoom Judge(long bytes, long cap, bool reserve, out bool known)
    {
        bytes = Math.Max(0, bytes);
        lock (_gate)
        {
            var reading = Current();
            known = reading.Known;
            if (!known) return BlindRoom.Reached;
            if (reading.Bytes >= cap || bytes > cap - reading.Bytes) return BlindRoom.Reached;
            var held = Saturating(reading.Bytes, _reserved);
            if (held >= cap || bytes > cap - held) return BlindRoom.Busy;
            if (reserve) _reserved = Saturating(_reserved, bytes);
            return BlindRoom.Fits;
        }
    }

    /// <summary>
    /// Leaves <paramref name="directory"/> (and everything in it) out of the walk until the returned handle is disposed: the folders a seed works in while it is received or
    /// applied. The cached reading is dropped so that the next decision measures without it.
    /// </summary>
    public IDisposable Exclude(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            _excluded.Add(full);
            _fresh = false;
        }
        return new Exclusion(this, full);
    }

    private sealed class Exclusion(BlindStoredSize owner, string full) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
            {
                owner._excluded.Remove(full);
                owner._fresh = false;
            }
        }
    }

    /// <summary>Drops the cached reading: the next decision walks again (a seed has ended, and its staging has gone or become the database).</summary>
    public void Invalidate()
    {
        lock (_gate) _fresh = false;
    }

    /// <summary>A write ended: its reservation is given back and what it really wrote is added to the reading until the next walk sees it.</summary>
    public void Release(long reserved, long written)
    {
        lock (_gate)
        {
            _reserved = Math.Max(0, _reserved - Math.Max(0, reserved));
            _written = Saturating(_written, Math.Max(0, written));
        }
    }

    /// <summary>What the writes in flight have reserved.</summary>
    public long Reserved
    {
        get
        {
            lock (_gate) return _reserved;
        }
    }

    /// <summary>
    /// A fresh walk that leaves out <paramref name="excludedDirectory"/> (a seed's own staging, which the caller counts exactly), and caches nothing. For the seed that is arriving:
    /// "what the node holds without this upload" is the number its staged bytes are added to.
    /// </summary>
    public BlindStoredReading MeasureExcluding(string excludedDirectory)
    {
        lock (_gate) return Measure([Path.GetFullPath(excludedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar]);
    }

    /// <summary>Brings the measurement up to date when it is stale. Walks, so it is called BEFORE the request is admitted, never inside one.</summary>
    public void EnsureFresh()
    {
        lock (_gate)
        {
            if (_fresh && time.GetUtcNow() - _measuredAt < CacheFor) return;
            _cached = Measure();
            _written = 0;
            _measuredAt = time.GetUtcNow();
            _fresh = true;
        }
    }

    private BlindStoredReading Measure(IReadOnlyCollection<string>? alsoExcluded = null)
    {
        try
        {
            var skip = alsoExcluded is { Count: > 0 } ? _excluded.Concat(alsoExcluded).ToList() : _excluded;
            var bytes = FileLength(DbPath) + FileLength(DbPath + "-wal") + DirSize(MediaDir) + DirSize(BaselinesDir)
                + DirSize(StagingDir, skip) + DirSize(CutoverDir, skip);
            return new BlindStoredReading(true, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A store this node cannot look into is not "empty": a storage limit waits until it can be judged.
            return BlindStoredReading.Unknown;
        }
    }

    private string DbPath => Path.Combine(dataPath, "beememorybank.db");
    private string MediaDir => Path.Combine(dataPath, "media");
    private string BaselinesDir => Path.Combine(dataPath, "baselines");

    // What the node writes into for a peer (BlindRoleStartup.TempDir, BlindSeedCutover.DirOf): written here by name, because this file stays free of the services around it.
    private string StagingDir => Path.Combine(dataPath, "blind-tmp");
    private string CutoverDir => Path.Combine(dataPath, "blind-cutover");

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long Saturating(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private static long DirSize(string dir, IReadOnlyCollection<string>? skip = null)
    {
        if (!Directory.Exists(dir)) return 0;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (skip is { Count: > 0 } && Path.GetFullPath(file) is var full && skip.Any(s => full.StartsWith(s, comparison))) continue;
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* a file being written or swept mid-walk is not worth failing the walk over */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // A folder of staging that the node's own cleanup removed while it was being walked: what was counted before it went is what there was.
        }
        return total;
    }
}

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What the meter knows of the bytes an account holds: a number, or an honest "unknown" with the reason. Unknown is never zero.</summary>
public sealed record BlindStorageReading(bool Known, long Bytes, string? Problem)
{
    public static BlindStorageReading Of(long bytes) => new(true, bytes, null);

    public static BlindStorageReading Unknown(string problem) => new(false, 0, problem);
}

/// <summary>
/// How many bytes an account holds on this device, kept LIVE. The number is a baseline (a walk over the files the account owns: the database with its WAL, the media, the sealed
/// baselines, the replica work folder with a partial download in it, the backups, the log and usage file) PLUS what has been charged since that walk (every byte the network brought in)
/// PLUS what is reserved for work that has begun but not finished (a download whose length is announced, the peak footprint of a first load or a backup). The limit guard decides on that
/// live number, so a round that stores several blobs within one minute sees each one the moment it arrives. The walk is only the periodic re-sync of the counters (<see cref="CacheFor"/>, and
/// after every round and job); it is never what a decision waits for or trusts alone.
///
/// <para><b>Fresh after every batch.</b> What the network brought in is charged as it is read, but a batch that has been PERSISTED can have grown the folders by more than it brought in (the
/// database and its index, a decoded payload, an extraction): every place that finishes a batch (an applied page of events, a stored blob, a baseline step, a backup step) calls
/// <see cref="Invalidate"/>, and the NEXT admission walks again before it decides (<see cref="EnsureFresh"/>), whatever the age of the last walk. The 60 seconds of <see cref="CacheFor"/> are only
/// for what a screen shows (<see cref="Reading"/>). What is not seen is bounded by one batch: the growth of a batch in flight, which the next admission sees.</para>
///
/// <para><b>Unknown is not zero.</b> A root that cannot be read, a root (or any folder on the way to it) that is a link, a link found inside owned data, or a walk that fails for any reason other
/// than "that file is gone" leaves the meter <see cref="BlindStorageReading.Known"/> = false, and the guard then pauses an account that has a storage limit. The walk looks at every entry
/// without following links and does not rely on "ignore what cannot be read".</para>
/// </summary>
public sealed class BlindStoredBytes
{
    /// <summary>How long a walk stays the baseline before <see cref="Reading"/> walks again (the periodic re-sync).</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly Func<IEnumerable<string>> _roots;
    private readonly TimeProvider _time;
    private readonly string? _safeBase;
    private readonly Action<string>? _walkHook;
    private readonly object _gate = new();
    private readonly object _walkGate = new();
    private long? _baseline;
    private string? _unknown;
    private DateTimeOffset? _at;
    private long _charged;
    private long _reserved;
    private long _jobReserved;
    private bool _stale;

    /// <param name="roots">The files and folders the account owns (a file counts as it is, a folder as everything below it).</param>
    public BlindStoredBytes(Func<IEnumerable<string>> roots, TimeProvider time) : this(roots, time, null, null)
    {
    }

    /// <param name="safeBase">The account's data folder: it, and every component from it down to each root, must be a real folder or file (no link). Null: each root is judged by itself.</param>
    /// <param name="walkHook">Tests only: called with each folder just before it is listed (it may throw what listing could throw, or change the folder).</param>
    internal BlindStoredBytes(Func<IEnumerable<string>> roots, TimeProvider time, string? safeBase, Action<string>? walkHook)
    {
        _roots = roots;
        _time = time;
        _safeBase = safeBase;
        _walkHook = walkHook;
    }

    // ---- reading ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The live number, after a re-sync walk when the last one is older than <see cref="CacheFor"/> (or there was none).</summary>
    public BlindStorageReading Reading()
    {
        EnsureMeasured();
        return Peek();
    }

    /// <summary>The live number as it is now, with no walk (what a decision made under the shared budget lock uses). Unknown when no walk has ever succeeded.</summary>
    public BlindStorageReading Peek()
    {
        lock (_gate)
        {
            if (_baseline is not { } baseline) return BlindStorageReading.Unknown(_unknown ?? "The stored size has not been measured yet.");
            return BlindStorageReading.Of(BlindMath.AddSat(BlindMath.AddSat(baseline, _charged), _reserved));
        }
    }

    /// <summary>The live number for a screen (0 when it is unknown: use <see cref="Reading"/> where unknown must be told apart from zero).</summary>
    public long Get() => Reading().Bytes;

    /// <summary>Walks now (after a job: a first load, a backup, a baseline, a delete changed what is held) and returns the new live number.</summary>
    public long Refresh()
    {
        Resync();
        return Peek().Bytes;
    }

    /// <summary>Walks if there is no baseline yet or it is older than <see cref="CacheFor"/>. Takes no lock that a decision holds: call it BEFORE asking the guard.</summary>
    public void EnsureMeasured()
    {
        bool stale;
        lock (_gate)
        {
            if (_at is not { } at) stale = true;
            else
            {
                var age = _time.GetUtcNow() - at;
                // An unknown result is tried again soon (a folder that could not be read a moment ago may be readable now); a number waits for its minute.
                stale = _baseline is null ? age >= TimeSpan.FromSeconds(5) : age >= CacheFor;
            }
        }
        if (stale) Resync();
    }

    /// <summary>A batch has been persisted: the next admission walks the folders again before it decides. Cheap (a flag): it can be called after every page, blob or step.</summary>
    public void Invalidate()
    {
        lock (_gate) _stale = true;
    }

    /// <summary>True from <see cref="Invalidate"/> until the next walk begins.</summary>
    internal bool IsStale
    {
        get
        {
            lock (_gate) return _stale;
        }
    }

    /// <summary>
    /// What a decision about to be taken calls (before it takes the budget lock): walks when a batch was persisted since the last walk, and otherwise as <see cref="EnsureMeasured"/>. A screen
    /// calls <see cref="EnsureMeasured"/> instead, and keeps its 60 seconds.
    /// </summary>
    internal void EnsureFresh()
    {
        bool stale;
        lock (_gate) stale = _stale;
        if (stale) Resync();
        else EnsureMeasured();
    }

    /// <summary>
    /// The periodic re-sync: walks the owned files and makes the result the new baseline. What was charged while the walk ran is kept (the walk may or may not have seen it: counting it twice for
    /// a moment is the safe side); what was charged before it began is now in the walk.
    /// </summary>
    public void Resync()
    {
        lock (_walkGate)
        {
            long chargedBefore;
            lock (_gate)
            {
                chargedBefore = _charged;
                _stale = false; // a batch persisted while the walk runs sets it again: the walk may have missed it
            }
            var reading = Walk();
            lock (_gate)
            {
                if (reading.Known)
                {
                    _baseline = reading.Bytes;
                    _unknown = null;
                    _charged = BlindMath.SubFloor(_charged, chargedBefore);
                }
                else
                {
                    _baseline = null;
                    _unknown = reading.Problem;
                }
                _at = _time.GetUtcNow();
            }
        }
    }

    // ---- live accounting (used under the shared budget lock by the guard) ----------------------------------------------------------------------

    /// <summary>Sets bytes aside for work that has begun (a download whose length is announced). The caller has checked that they fit.</summary>
    internal void Reserve(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _reserved = BlindMath.AddSat(_reserved, bytes);
    }

    /// <summary>Gives back bytes that were set aside and not used.</summary>
    internal void Unreserve(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _reserved = BlindMath.SubFloor(_reserved, bytes);
    }

    /// <summary>Bytes that arrived and are being stored: the reservation they were covered by becomes charged storage, with no moment in which they are counted twice or not at all.</summary>
    internal void ConvertReserved(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _reserved = BlindMath.SubFloor(_reserved, bytes);
            _charged = BlindMath.AddSat(_charged, bytes);
        }
    }

    /// <summary>Bytes that arrived and are being stored, with no reservation behind them.</summary>
    internal void Charge(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _charged = BlindMath.AddSat(_charged, bytes);
    }

    /// <summary>Sets bytes aside for the whole of a long job (the peak footprint beyond the archive: its extraction and the installed or encrypted result); <see cref="ReleaseJob"/> gives them back.</summary>
    internal void ReserveJob(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _reserved = BlindMath.AddSat(_reserved, bytes);
            _jobReserved = BlindMath.AddSat(_jobReserved, bytes);
        }
    }

    /// <summary>The long job ended (any way): what it set aside is given back. The next walk puts what it really left in the baseline.</summary>
    public void ReleaseJob()
    {
        lock (_gate)
        {
            _reserved = BlindMath.SubFloor(_reserved, _jobReserved);
            _jobReserved = 0;
        }
    }

    // ---- the walk --------------------------------------------------------------------------------------------------------------------------

    private BlindStorageReading Walk()
    {
        long total = 0;
        foreach (var root in _roots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var measured = Measure(root);
            if (!measured.Known) return measured;
            total = BlindMath.AddSat(total, measured.Bytes);
        }
        return BlindStorageReading.Of(total);
    }

    private BlindStorageReading Measure(string path)
    {
        // Every component down to the root is looked at, without following, BEFORE anything is read: a root that is a link would be counted for what lies behind it.
        var problem = _safeBase is null
            ? BlindPathSafety.IsLink(path) ? "A folder the app keeps data in is a link (a symbolic link or a junction). Nothing was measured through it." : null
            : BlindPathSafety.ComponentProblem(_safeBase, path);
        if (problem is not null) return BlindStorageReading.Unknown(problem);

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return BlindStorageReading.Of(0); // not there: it holds nothing
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BlindStorageReading.Unknown("A file or folder the app keeps data in could not be examined (" + ex.GetType().Name + ").");
        }

        if (!attributes.HasFlag(FileAttributes.Directory)) return LengthOf(new FileInfo(path));
        var walked = WalkFolder(path);
        if (!walked.Known) return walked;

        // A link that took the place of the root, or of a folder on the way to it, while the walk was under way: what was counted may be what lies behind it.
        var after = _safeBase is null ? null : BlindPathSafety.ComponentProblem(_safeBase, path);
        return after is null ? walked : BlindStorageReading.Unknown(after);
    }

    private BlindStorageReading WalkFolder(string root)
    {
        long sum = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                _walkHook?.Invoke(folder);
                if (BlindPathSafety.IsLink(folder))
                    return BlindStorageReading.Unknown("A link was found inside the data the app keeps. Nothing was measured through it.");
                // Listed whole, here, so that a failure to list is caught here and is never taken for an empty folder.
                entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos()];
            }
            catch (DirectoryNotFoundException)
            {
                continue; // removed while the walk was under way: it holds nothing now
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return BlindStorageReading.Unknown("A folder the app keeps data in could not be read (" + ex.GetType().Name + ").");
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return BlindStorageReading.Unknown("A file the app keeps data in could not be examined (" + ex.GetType().Name + ").");
                }
                if (attributes == (FileAttributes)(-1)) continue; // gone since the listing
                // The app makes no links: one inside what it owns is not counted and not followed, and the number is unknown (it cannot say what is behind it).
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                    return BlindStorageReading.Unknown("A link was found inside the data the app keeps. Nothing was measured through it.");

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry.FullName);
                    continue;
                }
                var length = LengthOf(new FileInfo(entry.FullName));
                if (!length.Known) return length;
                sum = BlindMath.AddSat(sum, length.Bytes);
            }
        }
        return BlindStorageReading.Of(sum);
    }

    private static BlindStorageReading LengthOf(FileInfo file)
    {
        try
        {
            return BlindStorageReading.Of(file.Length);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return BlindStorageReading.Of(0); // gone between the listing and now
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BlindStorageReading.Unknown("A file the app keeps data in could not be measured (" + ex.GetType().Name + ").");
        }
    }
}

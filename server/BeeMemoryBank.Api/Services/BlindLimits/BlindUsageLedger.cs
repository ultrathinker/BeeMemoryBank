using System.Text.Json;

namespace BeeMemoryBank.Api.Services.BlindLimits;

/// <summary>The answer of a judgement against a cap: it fits; the cap is reached by what the day (or the store) holds; or it would fit but other requests in flight have reserved the room.</summary>
public enum BlindRoom
{
    Fits,
    Reached,
    Busy,
}

/// <summary>
/// What this node moved for the mesh per local day, in <c>{dataPath}/blind/usage.json</c> beside
/// the settings: request and response bodies of the routes that carry the mesh's data, exact,
/// summed as one number a day (headers and TLS framing are not counted — a screen says "about"
/// when it shows this). The rules are the blind apps' (<c>BlindUsageLedger</c> in Blind.AppCore —
/// read and copied, not referenced: the server holds no account), because the two must not drift:
///
/// <para><b>The day only moves forward.</b> "Today" is the local calendar date, but never less than
/// the highest date this ledger has seen (the high-water date is saved with the file): a clock set
/// back, or a time zone change that makes the date earlier, does not start a new, empty day and so
/// does not refill the budget. Daylight-saving days are 23 or 25 hours long and are one day each,
/// because a day is a date, not a span.</para>
///
/// <para><b>Fail closed.</b> A file that exists and cannot be read (locked, no permission) or is
/// not what this build writes makes the counts <see cref="IsAvailable"/> = false: while a traffic
/// limit is in force the node then answers paused rather than serve against a day nobody knows,
/// and nothing is written over the file — the operator of THIS node repairs it (it is their file on
/// their machine; unlike an app copy, nothing of the mesh depends on it). Counting goes on in
/// memory, and a save that fails (a full disk) is retried every few seconds until it works: the
/// first that does makes the counts available again. A missing file is a node that never counted
/// anything and starts empty.</para>
///
/// <para>Flushed at the end of every counted request and on its own every 4 MB counted, so a crash
/// loses at most one request's tail.</para>
///
/// <para><b>Reservations.</b> Admission and counting are two moments, and between them N requests that
/// all read the same day's use would all pass. So the ledger also holds what admitted requests may
/// still move (<see cref="Judge"/> with a reservation, taken under the same lock that reads the day), and a request
/// gives its reservation back with what it really moved (<see cref="Release"/>) - in the same locked
/// step, so the bytes are never in neither number. A request that moves more than it reserved grows
/// its reservation (<see cref="Grow"/>) as it goes. Reservations live in memory only: they die with
/// the process, which also ends every request that held one.</para>
/// </summary>
public sealed class BlindUsageLedger
{
    private const int KeepDays = 31;
    private const long FlushEveryBytes = 4L << 20;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;

    private readonly SortedDictionary<DateOnly, long> _days = [];
    private DateOnly _highWater = DateOnly.MinValue;
    private bool _loaded;
    private bool _dirty;
    private long _bytesSinceFlush;
    private string? _problem;
    private bool _permanent;
    private DateTimeOffset _nextRetry;
    private string? _saveProblem;
    private DateTimeOffset _nextSaveRetry;
    private long _reserved;

    public BlindUsageLedger(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
    }

    /// <summary>False while the counts cannot be read or saved: a traffic limit cannot be checked against them.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                RetrySaveLocked();
                return _problem is null && _saveProblem is null;
            }
        }
    }

    /// <summary>Why the counts are unavailable, in words without a path, or null.</summary>
    public string? UnavailableReason
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                RetrySaveLocked();
                return _problem ?? _saveProblem;
            }
        }
    }

    /// <summary>The local date of now, or the highest date seen if that is later (the day never moves back).</summary>
    public DateOnly Today()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return AdvanceDay();
        }
    }

    /// <summary>The instant the day after <see cref="Today"/> begins in the node's time zone: when a daily limit lifts.</summary>
    public DateTimeOffset NextLocalMidnight() => StartOf(Today().AddDays(1));

    /// <summary>
    /// The first instant of the local date <paramref name="day"/>. A local midnight that happens twice (the clocks go back at 01:00 to 00:00) begins the day at the FIRST of them - the
    /// date is that day's from then on - and one that does not happen (the clocks go forward from 00:00 to 01:00) begins it at the change, when the clock first shows the new date.
    /// </summary>
    private DateTimeOffset StartOf(DateOnly day)
    {
        var zone = _time.LocalTimeZone;
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (zone.IsAmbiguousTime(local))
            return zone.GetAmbiguousTimeOffsets(local).Select(offset => new DateTimeOffset(local, offset).ToUniversalTime()).Min();
        // A midnight that does not exist has the zone's standard offset here, which is the offset in force just before the change: the change itself.
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>What <paramref name="day"/> moved (zero for a day with no record).</summary>
    public long Usage(DateOnly day)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _days.TryGetValue(day, out var bytes) ? bytes : 0;
        }
    }

    /// <summary>What admitted requests may still move: the bytes reserved for the requests in flight, beyond what the day has counted.</summary>
    public long Reserved
    {
        get
        {
            lock (_gate) return _reserved;
        }
    }

    /// <summary>
    /// Judges a request that may move <paramref name="bytes"/> against the daily <paramref name="cap"/> and, with <paramref name="reserve"/>, reserves them when they fit - one locked
    /// step, so concurrent requests cannot all pass against the same remaining budget. The two refusals are told apart: <see cref="BlindRoom.Reached"/> when the day's own count
    /// leaves no room (it lifts at the next local midnight), <see cref="BlindRoom.Busy"/> when the day would have room but other requests in flight have reserved it (it lifts when
    /// they end, which may be a minute from now).
    /// </summary>
    public BlindRoom Judge(long bytes, long cap, bool reserve)
    {
        bytes = Math.Max(0, bytes);
        lock (_gate)
        {
            EnsureLoaded();
            var used = Usage(AdvanceDay());
            if (cap <= 0 || used >= cap || Saturating(used, bytes) > cap) return BlindRoom.Reached;
            if (Saturating(Saturating(used, _reserved), bytes) > cap) return BlindRoom.Busy;
            if (reserve) _reserved = Saturating(_reserved, bytes);
            return BlindRoom.Fits;
        }
    }

    /// <summary>A request that moves more than it reserved reserves what it has moved, so that the requests that come after it see it.</summary>
    public void Grow(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _reserved = Saturating(_reserved, bytes);
    }

    /// <summary>The request ended: its <paramref name="reserved"/> bytes are given back and what it <paramref name="moved"/> is counted, in one step.</summary>
    public void Release(long reserved, long moved)
    {
        lock (_gate)
        {
            _reserved = Math.Max(0, _reserved - Math.Max(0, reserved));
            Add(moved);
        }
    }

    /// <summary>What today moved so far.</summary>
    public long UsageToday()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return Usage(AdvanceDay());
        }
    }

    /// <summary>Counts <paramref name="bytes"/> that moved for the mesh. Counted in memory even while the file cannot be written.</summary>
    public void Add(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            EnsureLoaded();
            var counters = AdvanceDay();
            _days[counters] = Saturating(_days.TryGetValue(counters, out var held) ? held : 0, bytes);
            _dirty = true;
            _bytesSinceFlush = Saturating(_bytesSinceFlush, bytes);
            if (_bytesSinceFlush >= FlushEveryBytes) TryFlushLocked();
        }
    }

    /// <summary>
    /// Writes what was counted since the last flush (the end of a request is one). Safe at any
    /// time; writes nothing while the file cannot be trusted. A write that fails does not throw:
    /// it makes the counts unavailable until a save works, and is retried on its own.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            EnsureLoaded();
            TryFlushLocked();
        }
    }

    private DateOnly AdvanceDay()
    {
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);
        if (local > _highWater)
        {
            _highWater = local;
            // Days older than the last 31 are dropped as the day moves on.
            var keepFrom = _highWater.AddDays(-(KeepDays - 1));
            foreach (var old in _days.Keys.Where(d => d < keepFrom).ToList()) _days.Remove(old);
            // The new high-water date is saved at once: a restart with the clock set back must still
            // find that this day has begun.
            _dirty = true;
            TryFlushLocked();
        }
        return _highWater;
    }

    private void TryFlushLocked()
    {
        if (_saveProblem is not null && _time.GetUtcNow() < _nextSaveRetry) return;
        try
        {
            FlushLocked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveProblem = $"the traffic counts could not be saved ({ex.GetType().Name})";
            _nextSaveRetry = _time.GetUtcNow() + RetryEvery;
        }
    }

    private void RetrySaveLocked()
    {
        if (_saveProblem is not null && _problem is null) TryFlushLocked();
    }

    private void FlushLocked()
    {
        // Never over a file that could not be trusted: it is the operator's to repair.
        if (!_dirty || _problem is not null) return;
        var keepFrom = _highWater.AddDays(-(KeepDays - 1));
        foreach (var old in _days.Keys.Where(d => d < keepFrom).ToList()) _days.Remove(old);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, Serialize());
        File.Move(tmp, _path, overwrite: true);
        _dirty = false;
        _bytesSinceFlush = 0;
        _saveProblem = null;
    }

    private byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(new UsageFile(1, _highWater,
            _days.Select(kv => new UsageDay(kv.Key, kv.Value)).ToList()), Json);

    private void EnsureLoaded()
    {
        if (_loaded) return;
        // A read that failed for a transient reason waits a few seconds before it is tried again; a
        // damaged file is not read again (the operator repairs it, or removes it).
        if (_problem is not null && !_permanent && _time.GetUtcNow() < _nextRetry) return;

        string text;
        try
        {
            if (Directory.Exists(_path))
                throw new IOException("a folder is where the file should be");
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No file: a node that never counted anything. The one case that starts empty.
            _loaded = true;
            _problem = null;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _problem = $"the traffic counts could not be read ({ex.GetType().Name})";
            _permanent = false;
            _nextRetry = _time.GetUtcNow() + RetryEvery;
            return;
        }

        UsageFile? file;
        try
        {
            file = JsonSerializer.Deserialize<UsageFile>(text, Json);
        }
        catch (JsonException)
        {
            Damaged("the traffic counts are damaged");
            return;
        }
        if (file is null || file.Version != 1)
        {
            Damaged(file is null ? "the traffic counts are empty or damaged" : "the traffic counts were written by another version");
            return;
        }
        foreach (var day in file.Days ?? [])
        {
            if (day.Bytes < 0)
            {
                Damaged("the traffic counts hold numbers that cannot be right");
                return;
            }
        }

        // What was counted in memory while the file could not be read adds to what the file says;
        // the day never moves back.
        foreach (var day in file.Days ?? [])
            _days[day.Day] = Saturating(_days.TryGetValue(day.Day, out var mine) ? mine : 0, day.Bytes);
        if (file.HighWater > _highWater) _highWater = file.HighWater;
        _loaded = true;
        _problem = null;
        _permanent = false;
    }

    private void Damaged(string problem)
    {
        _problem = problem;
        _permanent = true;
        _loaded = true; // judged once: the file is not read again until the operator repairs it
    }

    private static long Saturating(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private sealed record UsageFile(int Version, DateOnly HighWater, List<UsageDay>? Days);

    private sealed record UsageDay(DateOnly Day, long Bytes);
}

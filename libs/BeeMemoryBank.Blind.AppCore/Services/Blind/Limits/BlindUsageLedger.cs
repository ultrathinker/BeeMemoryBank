using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What one local calendar day used: requests, and bytes in and out (bodies exact, headers and TLS estimated, so a screen says "about").</summary>
public sealed record BlindDayUsage(DateOnly Day, long Requests, long BytesIn, long BytesOut)
{
    public long Traffic => BlindMath.AddSat(BytesIn, BytesOut);
}

/// <summary>
/// What an account used per local day: the last 31 days, in <c>usage.json</c> inside the account's folder (the root for account 1). Counted by the metering handler, flushed
/// after every round or job (<see cref="Flush"/>) and on its own every 4 MB or 30 seconds, so that a crash loses at most that much.
///
/// <para><b>The day only moves forward.</b> "Today" is the local calendar date, but never less than the highest date this ledger has seen (the high-water date is saved with the
/// file): a clock that is set back, or a time zone change that makes the date earlier, does not start a new, empty day and so does not refill the budget. Daylight-saving days are
/// 23 or 25 hours long and are one day each, because a day is a date, not a span.</para>
///
/// <para><b>A file that exists and cannot be trusted is not a fresh install.</b> A MISSING usage file is a copy that never counted anything: fine, the ledger starts empty. A file that exists
/// but cannot be read (access denied, locked, an I/O error), is malformed, or was written by another version leaves the ledger <see cref="IsAvailable"/> = false: it is never overwritten
/// (nothing is flushed while the state is unavailable), the guard pauses the account for its own reason (a limit of traffic or requests cannot be checked against a day nobody knows), and the
/// only way on is <see cref="StartNewLedger"/>, which a person asks for on purpose: it keeps a verified copy of the damaged file as <c>usage.json.damaged-&lt;utc&gt;</c> (the newest five are
/// kept), writes the new file over the old one in one step and reads it back, and only when all of that has worked is the ledger available again. A repair that fails (a full disk, a refused
/// permission) says so and leaves the ledger exactly as unavailable as it was. A read that failed for a transient reason (a file held by another process for a moment) is tried again by itself
/// and needs no repair. The damaged file is copied when the new ledger is started, not when the damage is found: moved earlier, the next start would find no file and read it as a fresh install.</para>
///
/// <para><b>A save that fails is a failure.</b> When the counts cannot be written (a full disk, a refused permission) the ledger is unavailable too, for as long as that lasts: the counts stay in
/// memory, a new save is tried every <see cref="RetryEvery"/>, and the first one that works makes the ledger available again. Admitting work against counts that nothing records would let a
/// restart read a missing or stale file as a fresh budget.</para>
///
/// <para>Counting goes on in memory while the state is unavailable (a request that must go out, such as the paused report, is still counted) and is saved by the new ledger.</para>
/// </summary>
public sealed class BlindUsageLedger
{
    public const int KeepDays = 31;
    public const long FlushEveryBytes = 4L << 20;
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);

    /// <summary>How soon a read that failed for a transient reason is tried again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SortedDictionary<DateOnly, long[]> _days = [];
    private DateOnly _highWater = DateOnly.MinValue;
    private bool _loaded;
    private bool _dirty;
    private long _bytesSinceFlush;
    private long _reservedTraffic;
    private DateTimeOffset _lastFlush;
    private readonly IBlindUsageFiles _files;
    private string? _problem;
    private bool _permanent;
    private DateTimeOffset _nextRetry;
    private string? _saveProblem;
    private DateTimeOffset _nextSaveRetry;

    public BlindUsageLedger(string path, TimeProvider time) : this(path, time, BlindUsageFiles.Default)
    {
    }

    /// <param name="files">The file system seam (tests make it fail).</param>
    internal BlindUsageLedger(string path, TimeProvider time, IBlindUsageFiles files)
    {
        _path = path;
        _time = time;
        _files = files;
        _lastFlush = time.GetUtcNow();
    }

    public TimeProvider Time => _time;

    // ---- availability ---------------------------------------------------------------------------------------------------------------------

    /// <summary>False while the usage file exists and cannot be trusted, or the counts cannot be saved (see the type's remarks): the guard pauses an account whose limits depend on it.</summary>
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

    /// <summary>
    /// Starts a new ledger ON PURPOSE (the repair a person asks for): a verified copy of the damaged file is kept as <c>usage.json.damaged-&lt;utc&gt;</c> (never over another file; the newest five
    /// are kept), the counts of this run are written over it in one step and read back, and only then is the ledger available again. Null on success, otherwise the sentence that says why not; the
    /// ledger is then exactly as unavailable as it was (a failed repair never opens the account).
    /// </summary>
    public string? StartNewLedger()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_problem is null)
                return _saveProblem is null
                    ? "The usage counts are in order; there is nothing to start again."
                    : "The usage counts are in order but cannot be saved right now (" + _saveProblem + ") - that is tried again by itself every few seconds.";

            // 1. The damaged file is copied aside and the copy checked. A file that cannot be read or copied is a refusal: nothing was changed.
            try
            {
                BlindUsageEvidence.KeepAside(_files, _path, _time);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "The damaged usage file could not be kept aside (" + ex.GetType().Name + "); nothing was changed.";
            }

            // 2. The new ledger over the old file, in one step, read back. Nothing of the in-memory state changes until it has worked.
            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);
            var highWater = local > _highWater ? local : _highWater;
            try
            {
                TrimOld(highWater);
                var bytes = Serialize(highWater);
                _files.Write(_path, bytes);
                var back = _files.Read(_path);
                if (back is null || !back.AsSpan().SequenceEqual(bytes)) throw new IOException("The new usage file could not be verified.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "The new usage file could not be written (" + ex.GetType().Name + "). The damaged file is kept aside and the account stays paused; try again when the disk allows.";
            }

            _highWater = highWater;
            _problem = null;
            _permanent = false;
            _loaded = true;
            _saveProblem = null;
            _dirty = false;
            _bytesSinceFlush = 0;
            _lastFlush = _time.GetUtcNow();
            BlindUsageEvidence.Prune(_files, _path);
            return null;
        }
    }

    // ---- the day ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The local date of now, or the highest date seen if that is later (the day never moves back).</summary>
    public DateOnly Today()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return AdvanceDay();
        }
    }

    /// <summary>The instant the day after <see cref="Today"/> begins in the device's time zone: when a daily limit lifts. Far ahead when the clock was set back.</summary>
    public DateTimeOffset NextLocalMidnight() => BlindDayMath.StartOf(Today().AddDays(1), _time.LocalTimeZone);

    // ---- counting --------------------------------------------------------------------------------------------------------------------------

    public void AddRequest() => Add(requests: 1, 0, 0);

    public void AddIn(long bytes) => Add(0, bytes, 0);

    public void AddOut(long bytes) => Add(0, 0, bytes);

    private void Add(long requests, long bytesIn, long bytesOut)
    {
        if (requests < 0 || bytesIn < 0 || bytesOut < 0) throw new ArgumentOutOfRangeException(nameof(requests), "A count is never negative.");
        lock (_gate) AddLocked(requests, bytesIn, bytesOut);
    }

    private void AddLocked(long requests, long bytesIn, long bytesOut)
    {
        EnsureLoaded();
        var counters = Counters(AdvanceDay());
        counters[0] = BlindMath.AddSat(counters[0], requests);
        counters[1] = BlindMath.AddSat(counters[1], bytesIn);
        counters[2] = BlindMath.AddSat(counters[2], bytesOut);
        _dirty = true;
        _bytesSinceFlush = BlindMath.AddSat(_bytesSinceFlush, BlindMath.AddSat(bytesIn, bytesOut));
        if (_bytesSinceFlush >= FlushEveryBytes || _time.GetUtcNow() - _lastFlush >= FlushEvery) TryFlushLocked();
    }

    // ---- traffic that is set aside, not yet spent (the guard's atomic admission; see BlindLimitGuard) -----------------------------------------

    /// <summary>Bytes of traffic set aside for requests and reads that have been admitted and not yet finished: they count against the day's limit as if spent.</summary>
    internal long ReservedTraffic
    {
        get
        {
            lock (_gate) return _reservedTraffic;
        }
    }

    internal void ReserveTraffic(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _reservedTraffic = BlindMath.AddSat(_reservedTraffic, bytes);
    }

    internal void UnreserveTraffic(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _reservedTraffic = BlindMath.SubFloor(_reservedTraffic, bytes);
    }

    /// <summary>Reserved traffic is spent: it leaves the reservation and enters the day's count in one step, so no other caller can see it counted twice or not at all.</summary>
    internal void SpendReserved(long bytes, bool inbound)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _reservedTraffic = BlindMath.SubFloor(_reservedTraffic, bytes);
            AddLocked(0, inbound ? bytes : 0, inbound ? 0 : bytes);
        }
    }

    // ---- reading ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>What <paramref name="day"/> used (zero for a day with no record).</summary>
    public BlindDayUsage Usage(DateOnly day)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _days.TryGetValue(day, out var c) ? new BlindDayUsage(day, c[0], c[1], c[2]) : new BlindDayUsage(day, 0, 0, 0);
        }
    }

    /// <summary>What today used.</summary>
    public BlindDayUsage UsageToday()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return Usage(AdvanceDay());
        }
    }

    /// <summary>The days that have a record, oldest first (at most <see cref="KeepDays"/>).</summary>
    public IReadOnlyList<BlindDayUsage> History()
    {
        lock (_gate)
        {
            EnsureLoaded();
            AdvanceDay();
            return _days.Select(kv => new BlindDayUsage(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2])).ToList();
        }
    }

    /// <summary>
    /// The average traffic per day over the last seven days ending today, from the first day that has a record (so a copy that is two days old is averaged over
    /// two days, not seven). Null when nothing was ever counted.
    /// </summary>
    public long? AverageTrafficPerDay(int days = 7)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var today = AdvanceDay();
            var from = today.AddDays(-(days - 1));
            var inWindow = _days.Where(kv => kv.Key >= from && kv.Key <= today).ToList();
            if (inWindow.Count == 0) return null;
            var first = inWindow.Min(kv => kv.Key);
            var span = today.DayNumber - first.DayNumber + 1;
            return inWindow.Aggregate(0L, (sum, kv) => BlindMath.AddSat(sum, BlindMath.AddSat(kv.Value[1], kv.Value[2]))) / Math.Max(1, span);
        }
    }

    // ---- saving ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes what was counted since the last flush (after every round or job). Safe to call at any time; writes nothing while the damaged file stands. A write that fails does not throw (it would
    /// replace the result of the round that called it): it makes the ledger unavailable until a save works (see the type's remarks).
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
            // The new high-water date is saved at once: a restart with the clock set back must still find that this day has begun.
            _dirty = true;
            TryFlushLocked();
        }
        return _highWater;
    }

    private long[] Counters(DateOnly day)
    {
        if (!_days.TryGetValue(day, out var c)) _days[day] = c = new long[3];
        return c;
    }

    /// <summary>
    /// A flush on the way (a count, a new day, the end of a round). The counts stay in memory when it fails, and the ledger is UNAVAILABLE until a save works: a new try is made every
    /// <see cref="RetryEvery"/> (not at every count), and the first success clears it.
    /// </summary>
    private void TryFlushLocked()
    {
        if (_saveProblem is not null && _time.GetUtcNow() < _nextSaveRetry) return;
        try
        {
            FlushLocked();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveProblem = "The usage counts could not be saved (" + ex.GetType().Name + ").";
            _nextSaveRetry = _time.GetUtcNow() + RetryEvery;
        }
    }

    /// <summary>Asked whenever availability is: a save that failed is tried again once its wait is over.</summary>
    private void RetrySaveLocked()
    {
        if (_saveProblem is not null && _problem is null) TryFlushLocked();
    }

    private void FlushLocked()
    {
        // Never over a file that could not be trusted: it is evidence, and only StartNewLedger puts a new ledger in its place.
        if (!_dirty || _problem is not null) return;
        TrimOld(_highWater);
        _files.Write(_path, Serialize(_highWater));
        _dirty = false;
        _bytesSinceFlush = 0;
        _lastFlush = _time.GetUtcNow();
        _saveProblem = null;
    }

    private void TrimOld(DateOnly highWater)
    {
        var keepFrom = highWater.AddDays(-(KeepDays - 1));
        foreach (var old in _days.Keys.Where(d => d < keepFrom).ToList()) _days.Remove(old);
    }

    private byte[] Serialize(DateOnly highWater) =>
        JsonSerializer.SerializeToUtf8Bytes(new UsageFile(1, highWater, _days.Select(kv => new UsageDay(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2])).ToList()), Json);

    private void EnsureLoaded()
    {
        if (_loaded) return;
        // A read that failed for a transient reason waits a few seconds before it is tried again; a damaged file is not read again (it stays as it is until it is repaired).
        if (_problem is not null && !_permanent && _time.GetUtcNow() < _nextRetry) return;

        byte[]? bytes;
        try
        {
            bytes = _files.Read(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is there and cannot be read now: not "absent". Tried again soon; until then the account's limits cannot be checked.
            _problem = "The usage counts could not be read (" + ex.GetType().Name + ").";
            _permanent = false;
            _nextRetry = _time.GetUtcNow() + RetryEvery;
            return;
        }

        if (bytes is null)
        {
            // No file: a copy that never counted anything. The one case that starts empty.
            _loaded = true;
            _problem = null;
            return;
        }

        UsageFile? file;
        try
        {
            file = JsonSerializer.Deserialize<UsageFile>(bytes, Json);
        }
        catch (JsonException)
        {
            Damaged("The usage file is damaged.");
            return;
        }
        if (file is null || file.Version != 1)
        {
            Damaged(file is null ? "The usage file is empty or damaged." : "The usage file was written by another version of this app.");
            return;
        }
        foreach (var day in file.Days ?? [])
        {
            if (day.Requests < 0 || day.BytesIn < 0 || day.BytesOut < 0)
            {
                Damaged("The usage file holds numbers that cannot be right.");
                return;
            }
        }

        // What was counted in memory while the file could not be read is added to what the file says; the day never moves back.
        foreach (var day in file.Days ?? [])
        {
            if (_days.TryGetValue(day.Day, out var mine))
            {
                mine[0] = BlindMath.AddSat(mine[0], day.Requests);
                mine[1] = BlindMath.AddSat(mine[1], day.BytesIn);
                mine[2] = BlindMath.AddSat(mine[2], day.BytesOut);
            }
            else
            {
                _days[day.Day] = [day.Requests, day.BytesIn, day.BytesOut];
            }
        }
        if (file.HighWater > _highWater) _highWater = file.HighWater;
        _loaded = true;
        _problem = null;
        _permanent = false;
    }

    private void Damaged(string problem)
    {
        _problem = problem;
        _permanent = true;
        _loaded = true; // judged once: the file is not read again until a person starts a new ledger
    }

    private sealed record UsageFile(int Version, DateOnly HighWater, List<UsageDay>? Days);

    private sealed record UsageDay(DateOnly Day, long Requests, long BytesIn, long BytesOut);
}

using System.Globalization;
using System.Text.Json;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What the whole device has used, across every account: today's traffic and the bytes stored, for the device limits (<see cref="BlindDeviceLimits"/>). One per process, shared by all
/// accounts' runtimes. The numbers are the accounts' own (each <see cref="BlindUsageLedger"/> and <see cref="BlindStoredBytes"/> registers here), so there is nothing to keep in step;
/// what the device keeps of its own is its high-water date, so that the device day too only moves forward (a clock set back cannot refill the device's budget by way of an account
/// that was idle).
///
/// <para><b>Every listed account counts.</b> The device's storage is the sum over every account the registry LISTS, not over the runtimes that happen to be up: an account in a repair state, one
/// whose start failed, one whose runtime was discarded, and one in the middle of a removal (its files still on disk) all still hold bytes. The host registers a meter over each listed account's
/// owned files for an account that has no runtime (<see cref="EnsureListed"/>), swaps a runtime's meter for such a meter when the runtime ends while the account is still listed
/// (<see cref="Detach"/>), and lets an account go (<see cref="Unregister"/>) only when its registry entry is gone. A listed account whose size cannot be measured makes the device's total unknown,
/// which blocks a device storage decision.</para>
///
/// <para><b>The one budget lock.</b> <see cref="Gate"/> is the lock under which every admission against a limit is decided AND charged (see <see cref="BlindLimitGuard"/>): the account's
/// limits and the device's are checked and the request, the bytes and the storage are set aside in one step, by whichever account asks, so two callers can never both pass on the same
/// remaining allowance.</para>
///
/// <para><b>The high-water file is judged like a usage file.</b> A missing one is a fresh install; one that exists and cannot be read, is malformed or has another version leaves the device
/// <see cref="IsAvailable"/> = false, is never overwritten, and pauses the accounts while the device has a traffic limit (a limit cannot be checked against a day nobody knows) until a person
/// calls <see cref="StartNewHighWater"/>, which keeps a verified copy of the damaged file aside (the newest five are kept), writes the new date over it in one step, reads it back, and only then
/// makes the device available again; a repair that fails leaves the device exactly as unavailable as it was. Without that, a clock set back on top of a damaged file would refill the device's budget.
/// A save that fails (a new day's date that cannot be written) makes the device unavailable too, until a save works: a date that nothing records is a day a restart can forget.</para>
/// </summary>
public sealed class BlindDeviceUsage
{
    private readonly object _gate = new();
    private readonly object _budgetGate = new();
    private readonly TimeProvider _time;
    private readonly string? _file;
    private readonly Func<BlindDeviceLimits> _limits;
    private readonly Dictionary<string, (BlindUsageLedger? Ledger, BlindStoredBytes Stored)> _accounts = new(StringComparer.Ordinal);
    private DateOnly _highWater = DateOnly.MinValue;
    private bool _loaded;
    private string? _problem;
    private bool _permanent;
    private DateTimeOffset _nextRetry;
    private readonly IBlindUsageFiles _files;
    private string? _saveProblem;
    private DateTimeOffset _nextSaveRetry;

    /// <param name="stateFile">Where the high-water date is kept (<c>device-usage.json</c> in the app's root); null for a composition with no device-wide state.</param>
    /// <param name="limits">The device limits as the person set them now (read at every check).</param>
    public BlindDeviceUsage(TimeProvider time, string? stateFile = null, Func<BlindDeviceLimits>? limits = null) : this(time, stateFile, limits, BlindUsageFiles.Default)
    {
    }

    /// <param name="files">The file system seam (tests make it fail).</param>
    internal BlindDeviceUsage(TimeProvider time, string? stateFile, Func<BlindDeviceLimits>? limits, IBlindUsageFiles files)
    {
        _time = time;
        _file = stateFile;
        _limits = limits ?? (() => BlindDeviceLimits.None);
        _files = files;
    }

    public BlindDeviceLimits Limits => _limits();

    /// <summary>The lock under which an admission is decided and charged. Taken before any lock of a ledger or a meter, never inside one.</summary>
    internal object Gate => _budgetGate;

    /// <summary>An account's ledger and stored bytes count towards the device from now on.</summary>
    public void Register(string accountId, BlindUsageLedger ledger, BlindStoredBytes stored)
    {
        lock (_gate) _accounts[accountId] = (ledger, stored);
    }

    /// <summary>An account that is no longer listed (its registry entry is gone) no longer counts.</summary>
    public void Unregister(string accountId)
    {
        lock (_gate) _accounts.Remove(accountId);
    }

    /// <summary>A listed account with no runtime: its owned files count through <paramref name="detached"/> (made only when the account is not registered yet).</summary>
    internal void EnsureListed(string accountId, Func<BlindStoredBytes> detached)
    {
        lock (_gate)
        {
            if (!_accounts.ContainsKey(accountId)) _accounts[accountId] = (null, detached());
        }
    }

    /// <summary>The runtime of a LISTED account ended: its files still count, through <paramref name="detached"/>; what it used today stays in the device's day.</summary>
    internal void Detach(string accountId, Func<BlindStoredBytes> detached)
    {
        lock (_gate)
        {
            _accounts[accountId] = (_accounts.TryGetValue(accountId, out var current) ? current.Ledger : null, detached());
        }
    }

    /// <summary>The accounts that count now.</summary>
    internal IReadOnlyList<string> RegisteredIds()
    {
        lock (_gate) return [.. _accounts.Keys];
    }

    // ---- availability of the device's own state --------------------------------------------------------------------------------------------

    /// <summary>False while the device's high-water file exists and cannot be trusted, or its date cannot be saved (see the type's remarks).</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                Load();
                RetrySave();
                return _problem is null && _saveProblem is null;
            }
        }
    }

    /// <summary>Why the device's counts are unavailable, in words without a path, or null.</summary>
    public string? UnavailableReason
    {
        get
        {
            lock (_gate)
            {
                Load();
                RetrySave();
                return _problem ?? _saveProblem;
            }
        }
    }

    /// <summary>
    /// The repair a person asks for on purpose: a verified copy of the damaged file is kept as <c>device-usage.json.damaged-&lt;utc&gt;</c> (the newest five are kept), the new high-water date is
    /// written over it in one step and read back, and only then is the device available again. Null on success, otherwise why not; the device is then exactly as unavailable as it was.
    /// </summary>
    public string? StartNewHighWater()
    {
        lock (_gate)
        {
            Load();
            if (_problem is null)
                return _saveProblem is null
                    ? "The device's usage counts are in order; there is nothing to start again."
                    : "The device's usage counts are in order but cannot be saved right now (" + _saveProblem + ") - that is tried again by itself every few seconds.";

            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);
            var highWater = local > _highWater ? local : _highWater;
            if (_file is not null)
            {
                try
                {
                    BlindUsageEvidence.KeepAside(_files, _file, _time);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return "The damaged device usage file could not be kept aside (" + ex.GetType().Name + "); nothing was changed.";
                }

                try
                {
                    var bytes = Serialize(highWater);
                    _files.Write(_file, bytes);
                    var back = _files.Read(_file);
                    if (back is null || !back.AsSpan().SequenceEqual(bytes)) throw new IOException("The new device usage file could not be verified.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return "The new device usage file could not be written (" + ex.GetType().Name + "). The damaged file is kept aside and the device stays paused; try again when the disk allows.";
                }
            }

            _highWater = highWater;
            _problem = null;
            _permanent = false;
            _loaded = true;
            _saveProblem = null;
            if (_file is not null) BlindUsageEvidence.Prune(_files, _file);
            return null;
        }
    }

    // ---- the day ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The device's day: the local date, or the highest one seen if that is later.</summary>
    public DateOnly Today()
    {
        lock (_gate)
        {
            Load();
            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);
            if (local > _highWater)
            {
                _highWater = local;
                Save();
            }
            return _highWater;
        }
    }

    /// <summary>The instant the device's next day begins, in the device's time zone (see <see cref="BlindUsageLedger.NextLocalMidnight"/>).</summary>
    public DateTimeOffset NextLocalMidnight() => BlindDayMath.StartOf(Today().AddDays(1), _time.LocalTimeZone);

    // ---- what the accounts have used ----------------------------------------------------------------------------------------------------------

    /// <summary>Bytes in and out, over every account, on the device's day.</summary>
    public long TrafficToday()
    {
        var day = Today();
        return Snapshot().Aggregate(0L, (sum, a) => BlindMath.AddSat(sum, a.Ledger?.Usage(day).Traffic ?? 0));
    }

    /// <summary>Requests, over every account, on the device's day.</summary>
    public long RequestsToday()
    {
        var day = Today();
        return Snapshot().Aggregate(0L, (sum, a) => BlindMath.AddSat(sum, a.Ledger?.Usage(day).Requests ?? 0));
    }

    /// <summary>What the accounts have used today PLUS what they have set aside for requests and reads in flight: what a limit is checked against (called under <see cref="Gate"/>).</summary>
    internal long TrafficUsedAndReserved()
    {
        var day = Today();
        return Snapshot().Aggregate(0L, (sum, a) => BlindMath.AddSat(sum, a.Ledger is null ? 0 : BlindMath.AddSat(a.Ledger.Usage(day).Traffic, a.Ledger.ReservedTraffic)));
    }

    /// <summary>Bytes held by every account together, for a screen (0 for what is unknown: see <see cref="StoredReading"/>).</summary>
    public long StoredBytes() => StoredReading().Bytes;

    /// <summary>The bytes held by every account together, after walking whatever is stale; unknown when any account's is.</summary>
    public BlindStorageReading StoredReading()
    {
        EnsureMeasured();
        return PeekStored();
    }

    /// <summary>The live total with no walk (under <see cref="Gate"/>): unknown when any account's is.</summary>
    internal BlindStorageReading PeekStored()
    {
        long total = 0;
        foreach (var account in Snapshot())
        {
            var reading = account.Stored.Peek();
            if (!reading.Known) return reading;
            total = BlindMath.AddSat(total, reading.Bytes);
        }
        return BlindStorageReading.Of(total);
    }

    /// <summary>Re-syncs the account counters that are stale. Takes no budget lock: call it before asking the guard.</summary>
    internal void EnsureMeasured()
    {
        foreach (var account in Snapshot()) account.Stored.EnsureMeasured();
    }

    /// <summary>The same for a decision about to be taken: an account whose batch was persisted since its last walk is walked again, whatever the age of that walk.</summary>
    internal void EnsureFresh()
    {
        foreach (var account in Snapshot()) account.Stored.EnsureFresh();
    }

    /// <summary>How many accounts count.</summary>
    public int AccountCount { get { lock (_gate) return _accounts.Count; } }

    private List<(BlindUsageLedger? Ledger, BlindStoredBytes Stored)> Snapshot()
    {
        lock (_gate) return [.. _accounts.Values];
    }

    // ---- the high-water file ----------------------------------------------------------------------------------------------------------------

    private void Load()
    {
        if (_loaded) return;
        if (_file is null)
        {
            _loaded = true;
            return;
        }
        // A read that failed for a transient reason is tried again after a few seconds; a damaged file stays as it is until it is repaired.
        if (_problem is not null && !_permanent && _time.GetUtcNow() < _nextRetry) return;

        byte[]? bytes;
        try
        {
            bytes = _files.Read(_file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _problem = "The device's usage counts could not be read (" + ex.GetType().Name + ").";
            _permanent = false;
            _nextRetry = _time.GetUtcNow() + BlindUsageLedger.RetryEvery;
            return;
        }

        if (bytes is null)
        {
            _loaded = true; // a fresh install: the device day starts from the clock
            _problem = null;
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var v) && v == 1
                && root.TryGetProperty("highWater", out var value) && value.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (date > _highWater) _highWater = date;
                _loaded = true;
                _problem = null;
                _permanent = false;
                return;
            }
        }
        catch (JsonException)
        {
        }
        _problem = "The device's usage file is damaged or was written by another version of this app.";
        _permanent = true;
        _loaded = true; // judged once: not read again until a person starts a new one
    }

    private byte[] Serialize(DateOnly highWater) =>
        System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"highWater\":\"" + highWater.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\"}");

    /// <summary>Saves the date. A save that fails makes the device unavailable until one works (a new try every few seconds, not at every call): a date nothing records is a day a restart can forget.</summary>
    private void Save()
    {
        // Never over a file that could not be trusted: it is evidence.
        if (_file is null || _problem is not null) return;
        if (_saveProblem is not null && _time.GetUtcNow() < _nextSaveRetry) return;
        try
        {
            _files.Write(_file, Serialize(_highWater));
            _saveProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveProblem = "The device's usage counts could not be saved (" + ex.GetType().Name + ").";
            _nextSaveRetry = _time.GetUtcNow() + BlindUsageLedger.RetryEvery;
        }
    }

    /// <summary>Asked whenever availability is: a save that failed is tried again once its wait is over.</summary>
    private void RetrySave()
    {
        if (_saveProblem is not null && _problem is null) Save();
    }
}

/// <summary>Where a local calendar day begins, as an instant: the one rule the account's and the device's "resumes at 00:00" share.</summary>
internal static class BlindDayMath
{
    /// <summary>
    /// The instant <paramref name="day"/> begins in <paramref name="zone"/>. A zone that skips its midnight (a spring-forward at 00:00) begins the day an hour later; a midnight that
    /// happens twice (a fall-back from 00:00 to 23:00) begins the day at the first of them.
    /// </summary>
    public static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var utc = zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}

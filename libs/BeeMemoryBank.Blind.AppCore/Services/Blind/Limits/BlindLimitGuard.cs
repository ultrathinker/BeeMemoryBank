using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The one place that says "stop": <see cref="Check"/> answers null (go on) or the <see cref="BlindPause"/> that holds an account's work. It is asked before a round or a long job starts,
/// before each request goes out and every 256 KB of a response (the metering handler), and by the scheduler. It reads the person's policy and the usage counters at every call, so a changed
/// setting or a new day takes effect at once, and it is cheap (counters in memory).
///
/// <para>Order, the person's own words first: paused by you, the network (and the battery) switches, then the account's traffic, requests and storage, then the device's traffic and storage.
/// <b>Zero means paused</b>, never "unlimited": a limit of 0 holds the account (for the reason of that limit, with no end but a change of the setting). A switch that is on and a device that
/// cannot say (null) waits: the guard is the data plan's friend.</para>
///
/// <para><b>Deciding and charging are one step.</b> <see cref="Check"/> only looks. What lets work through is <see cref="AdmitRequest"/>: under ONE lock shared by every account of the device
/// (<see cref="BlindDeviceUsage.Gate"/>) it checks the account's limits AND the device's, counts the request and sets aside the traffic and storage the request is known to need. Two callers
/// that race on the last request, or on the last 9 KB of a device's traffic, are therefore decided one after the other, and the second sees what the first took. What was set aside is spent as
/// the bytes really move (or given back, when the request is cancelled or the body is shorter than it said). Storage is live in the same way: every byte that arrives is charged to the account's
/// <see cref="BlindStoredBytes"/> as it is read, an announced length is set aside before the first byte, and a length nobody announced (chunked, missing, understated) may only go as far as
/// the room that is left: the stream stops there, with a pause, and what it was carrying is never applied. The periodic walk of the folders only re-syncs those counters.</para>
///
/// <para><b>Fail closed.</b> Usage counts that cannot be read or trusted, or a stored size that cannot be measured, pause an account that has a limit that depends on them (each for a reason of its own);
/// an account without such a limit is not stopped by them. Every sum saturates and every "does it fit" is asked as <c>planned &lt;= limit - held</c>, so a length near
/// <c>long.MaxValue</c> is a refusal and never a wrapped small number.</para>
/// </summary>
public sealed class BlindLimitGuard(
    IBlindAccountPolicySource policy, BlindUsageLedger ledger, BlindStoredBytes stored, BlindDeviceUsage device, IBlindDeviceConditions conditions)
{
    /// <summary>The pause in force, or null.</summary>
    /// <param name="plannedBytes">What the caller is about to add to the account's storage (a download's length): the storage limits are tested against stored bytes plus this, so that a
    /// download that cannot fit is refused BEFORE it starts. Traffic is not tested against it: a download longer than a day's allowance simply goes on for several days.</param>
    public BlindPause? Check(long plannedBytes = 0)
    {
        Prepare();
        lock (device.Gate) return Evaluate(policy.Current, device.Limits, plannedBytes, 0);
    }

    // ---- what the decisions are made on ------------------------------------------------------------------------------------------------------

    /// <summary>True when a storage limit (the account's or the device's) is set: only then do the storage counters decide anything.</summary>
    internal bool StorageLimited => policy.Current.Limits.MaxStoredBytes is not null || device.Limits.MaxStoredBytes is not null;

    /// <summary>Brings the stored-size counters up to date when they are stale. Walks, so it is called BEFORE the budget lock is taken, never under it.</summary>
    private void Prepare()
    {
        if (policy.Current.Limits.MaxStoredBytes is not null) stored.EnsureFresh();
        if (device.Limits.MaxStoredBytes is not null) device.EnsureFresh();
    }

    /// <summary>The decision itself, on the live counters. Called under the budget lock.</summary>
    private BlindPause? Evaluate(BlindAccountPolicy current, BlindDeviceLimits deviceLimits, long plannedStorage, long plannedTraffic)
    {
        var limits = current.Limits;

        if (current.PausedByUser) return new BlindPause(BlindPauseReasons.PausedByYou);

        // The device's own word on the network and on power. Not asked unless the switch is on; null with the switch on is "wait".
        if (current.UnmeteredOnly && conditions.IsMetered != false)
            return new BlindPause(BlindPauseReasons.MeteredNetwork, ConditionUnknown: conditions.IsMetered is null);
        if (current.PauseOnPowerSaving && conditions.IsPowerSaving != false)
            return new BlindPause(BlindPauseReasons.PowerSaving, ConditionUnknown: conditions.IsPowerSaving is null);

        // A limit of traffic or requests is checked against the day's counts: counts that cannot be trusted cannot check it.
        if ((limits.MaxTrafficBytesPerDay is not null || limits.MaxRequestsPerDay is not null) && !ledger.IsAvailable)
            return new BlindPause(BlindPauseReasons.UsageUnavailable);

        var today = ledger.UsageToday();
        var used = BlindMath.AddSat(today.Traffic, ledger.ReservedTraffic);
        if (limits.MaxTrafficBytesPerDay is { } traffic && (traffic == 0 || used >= traffic || plannedTraffic > 0 && !BlindMath.Fits(traffic, used, plannedTraffic)))
            return new BlindPause(BlindPauseReasons.Traffic, traffic == 0 ? null : ledger.NextLocalMidnight(), traffic);
        if (limits.MaxRequestsPerDay is { } requests && (requests == 0 || today.Requests >= requests))
            return new BlindPause(BlindPauseReasons.Requests, requests == 0 ? null : ledger.NextLocalMidnight(), requests);

        if (AccountStoragePause(current, plannedStorage) is { } disk) return disk;

        if (deviceLimits.MaxTrafficBytesPerDay is { } deviceTraffic)
        {
            if (!device.IsAvailable) return new BlindPause(BlindPauseReasons.DeviceUsageUnavailable);
            var deviceUsed = device.TrafficUsedAndReserved();
            if (deviceTraffic == 0 || deviceUsed >= deviceTraffic || plannedTraffic > 0 && !BlindMath.Fits(deviceTraffic, deviceUsed, plannedTraffic))
                return new BlindPause(BlindPauseReasons.DeviceTraffic, deviceTraffic == 0 ? null : device.NextLocalMidnight(), deviceTraffic);
        }

        return DeviceStoragePause(deviceLimits, plannedStorage);
    }

    private BlindPause? AccountStoragePause(BlindAccountPolicy current, long planned)
    {
        if (current.Limits.MaxStoredBytes is not { } disk) return null;
        planned = Math.Max(0, planned);
        if (disk == 0) return new BlindPause(BlindPauseReasons.Disk, null, disk, planned > 0 ? BlindMath.Needed(0, planned) : null);
        var reading = stored.Peek();
        if (!reading.Known) return new BlindPause(BlindPauseReasons.StorageUnknown);
        // "Reached" (held >= limit) holds the account even for a planned size of zero; otherwise the planned size must fit in what is left.
        if (reading.Bytes >= disk || !BlindMath.Fits(disk, reading.Bytes, planned))
            return new BlindPause(BlindPauseReasons.Disk, null, disk, planned > 0 ? BlindMath.Needed(reading.Bytes, planned) : null);
        return null;
    }

    private BlindPause? DeviceStoragePause(BlindDeviceLimits deviceLimits, long planned)
    {
        if (deviceLimits.MaxStoredBytes is not { } deviceDisk) return null;
        planned = Math.Max(0, planned);
        if (deviceDisk == 0) return new BlindPause(BlindPauseReasons.DeviceDisk, null, deviceDisk, planned > 0 ? BlindMath.Needed(0, planned) : null);
        var reading = device.PeekStored();
        if (!reading.Known) return new BlindPause(BlindPauseReasons.StorageUnknown);
        if (reading.Bytes >= deviceDisk || !BlindMath.Fits(deviceDisk, reading.Bytes, planned))
            return new BlindPause(BlindPauseReasons.DeviceDisk, null, deviceDisk, planned > 0 ? BlindMath.Needed(reading.Bytes, planned) : null);
        return null;
    }

    /// <summary>The storage reasons only (an announced length that cannot fit is refused before its first byte for these and for nothing else). Called under the budget lock.</summary>
    private BlindPause? StoragePause(long planned) => AccountStoragePause(policy.Current, planned) ?? DeviceStoragePause(device.Limits, planned);

    /// <summary>The traffic reasons only, for traffic that was not set aside beforehand. Called under the budget lock.</summary>
    private BlindPause? TrafficPause(long planned)
    {
        var limits = policy.Current.Limits;
        if (limits.MaxTrafficBytesPerDay is { } traffic)
        {
            var used = BlindMath.AddSat(ledger.UsageToday().Traffic, ledger.ReservedTraffic);
            if (traffic == 0 || used >= traffic || !BlindMath.Fits(traffic, used, planned))
                return new BlindPause(BlindPauseReasons.Traffic, traffic == 0 ? null : ledger.NextLocalMidnight(), traffic);
        }
        if (device.Limits.MaxTrafficBytesPerDay is { } deviceTraffic)
        {
            var used = device.TrafficUsedAndReserved();
            if (deviceTraffic == 0 || used >= deviceTraffic || !BlindMath.Fits(deviceTraffic, used, planned))
                return new BlindPause(BlindPauseReasons.DeviceTraffic, deviceTraffic == 0 ? null : device.NextLocalMidnight(), deviceTraffic);
        }
        return null;
    }

    /// <summary>How many more bytes of traffic the account and the device allow right now (long.MaxValue when neither has a limit). Under the budget lock.</summary>
    private long TrafficRoom()
    {
        var room = long.MaxValue;
        if (policy.Current.Limits.MaxTrafficBytesPerDay is { } traffic)
            room = traffic == 0 ? 0 : BlindMath.SubFloor(traffic, BlindMath.AddSat(ledger.UsageToday().Traffic, ledger.ReservedTraffic));
        if (device.Limits.MaxTrafficBytesPerDay is { } deviceTraffic)
            room = Math.Min(room, deviceTraffic == 0 ? 0 : BlindMath.SubFloor(deviceTraffic, device.TrafficUsedAndReserved()));
        return room;
    }

    /// <summary>How many more bytes the account and the device may store right now (long.MaxValue when neither has a limit); null when a size is unknown. Under the budget lock.</summary>
    private long? StorageRoom(out BlindPause? unknown)
    {
        unknown = null;
        var room = long.MaxValue;
        if (policy.Current.Limits.MaxStoredBytes is { } disk)
        {
            var reading = stored.Peek();
            if (!reading.Known)
            {
                unknown = new BlindPause(BlindPauseReasons.StorageUnknown);
                return null;
            }
            room = disk == 0 ? 0 : BlindMath.SubFloor(disk, reading.Bytes);
        }
        if (device.Limits.MaxStoredBytes is { } deviceDisk)
        {
            var reading = device.PeekStored();
            if (!reading.Known)
            {
                unknown = new BlindPause(BlindPauseReasons.StorageUnknown);
                return null;
            }
            room = Math.Min(room, deviceDisk == 0 ? 0 : BlindMath.SubFloor(deviceDisk, reading.Bytes));
        }
        return room;
    }

    // ---- admission ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lets a request through, or throws <see cref="BlindPausedException"/>. In one step under the budget lock: the checks of <see cref="Check"/> (with the request's own cost added to the traffic
    /// that has to fit), the request counted, and its cost set aside: the headers out (spent at once), the body if its length is announced, and the headers of the answer.
    /// </summary>
    /// <param name="knownBodyBytes">The announced length of the request's body (0 when there is none or it is not known; an unannounced body is admitted write by write).</param>
    internal BlindRequestAdmission AdmitRequest(long knownBodyBytes)
    {
        Prepare();
        var cost = BlindMath.AddSat(BlindMath.AddSat(BlindMeteringTap.OverheadBytes, BlindMeteringTap.OverheadBytes), Math.Max(0, knownBodyBytes));
        lock (device.Gate)
        {
            if (Evaluate(policy.Current, device.Limits, 0, cost) is { } pause) throw new BlindPausedException(pause);
            ledger.AddRequest();
            ledger.ReserveTraffic(cost);
            ledger.SpendReserved(BlindMeteringTap.OverheadBytes, inbound: false);
            return new BlindRequestAdmission(this, BlindMath.SubFloor(cost, BlindMeteringTap.OverheadBytes), exempt: false);
        }
    }

    /// <summary>A request that is counted and never stopped: the paused report to the source must go out whatever the limits say (about 3 KB, counted).</summary>
    internal BlindRequestAdmission AdmitExempt()
    {
        ledger.AddRequest();
        ledger.AddOut(BlindMeteringTap.OverheadBytes);
        return new BlindRequestAdmission(this, 0, exempt: true);
    }

    /// <summary>
    /// A long job's package is about to be downloaded: sets aside, for the whole of the job, the peak footprint BEYOND the archive that the HTTP layer has already set aside (its extraction, the
    /// installed copy or the encrypted backup beside it): <paramref name="factor"/> - 1 times the length. Refuses, before the first byte, when that does not fit. The job gives it back when it ends
    /// (<see cref="BlindStoredBytes.ReleaseJob"/>).
    /// </summary>
    /// <param name="archiveLength">The announced length of the whole archive, whatever part of it is already on disk.</param>
    /// <param name="factor">How many times the archive's size the job needs at its peak: 3 for a first load or a baseline update (archive, extraction, installed copy), 4 for a backup (the check's extraction and the two copies the backup writes).</param>
    public void AdmitPackage(long archiveLength, int factor)
    {
        if (!StorageLimited) return;
        Prepare();
        var extra = BlindMath.MulSat(Math.Max(0, factor - 1), Math.Max(0, archiveLength));
        lock (device.Gate)
        {
            if (StoragePause(extra) is { } pause) throw new BlindPausedException(pause);
            stored.ReserveJob(extra);
        }
    }

    // ---- the pieces the admission calls under the budget lock -------------------------------------------------------------------------------

    internal object Gate => device.Gate;
    internal BlindUsageLedger Ledger => ledger;
    internal BlindStoredBytes Stored => stored;

    internal BlindPause? StoragePauseLocked(long planned) => StoragePause(planned);

    internal BlindPause? TrafficPauseLocked(long planned) => TrafficPause(planned);

    internal long TrafficRoomLocked() => TrafficRoom();

    internal long? StorageRoomLocked(out BlindPause? unknown) => StorageRoom(out unknown);

    /// <summary>The full decision again, for a read that reaches a 256 KB boundary or finds no room: names the reason.</summary>
    internal BlindPause? EvaluateLocked() => Evaluate(policy.Current, device.Limits, 0, 0);

    internal void PrepareMeasurements() => Prepare();
}

/// <summary>
/// What one admitted request has set aside and not yet spent, held until the request and its response are done: the traffic of its body and of the answer's headers, and the storage of an
/// announced response. Every method that moves a counter does it under the device's budget lock. Disposing gives back whatever was not used (a cancelled request, a body shorter than it said,
/// a response that was not read to its end), and a finalizer does the same for one that was dropped without being disposed, so a reservation can never outlive its request.
///
/// <para><b>Closed is closed.</b> Disposing is idempotent and final. After it, the methods that would set something aside (<see cref="BeginWrite"/>, <see cref="ReserveResponseStorage"/>,
/// <see cref="BeginRead"/>) refuse with <see cref="ObjectDisposedException"/>, and every other method does nothing at all: a late call can neither set aside what nobody will give back nor give
/// back what was already given back. The counting streams own the release (<see cref="CountingReadStream"/> disposes the admission when it ends, when it is disposed, and when the answer's
/// content is), so a caller that closes only the stream, or keeps the content after it, cannot leave a reservation behind.</para>
/// </summary>
internal sealed class BlindRequestAdmission : IDisposable
{
    private readonly BlindLimitGuard _guard;
    private readonly bool _exempt;
    private long _traffic;
    private long _storage;
    private long _readTraffic;
    private long _readExtraStorage;
    private bool _readLimited;
    private long _sinceCheck;
    private int _disposed;

    public BlindRequestAdmission(BlindLimitGuard guard, long reservedTraffic, bool exempt)
    {
        _guard = guard;
        _traffic = reservedTraffic;
        _exempt = exempt;
    }

    ~BlindRequestAdmission() => Dispose();

    private bool Closed => Volatile.Read(ref _disposed) != 0;

    private void ThrowIfClosed()
    {
        if (Closed) throw new ObjectDisposedException(nameof(BlindRequestAdmission));
    }

    /// <summary>Makes sure the next <paramref name="count"/> bytes of the request's body are covered (a body whose length was not announced, or that is longer than it said, is admitted write by write). Throws a pause when they would not fit.</summary>
    public void BeginWrite(long count)
    {
        ThrowIfClosed();
        if (_exempt || count <= 0) return;
        lock (_guard.Gate)
        {
            ThrowIfClosed();
            var extra = BlindMath.SubFloor(count, _traffic);
            if (extra > 0)
            {
                if (_guard.TrafficPauseLocked(extra) is { } pause) throw new BlindPausedException(pause);
                _guard.Ledger.ReserveTraffic(extra);
                _traffic = BlindMath.AddSat(_traffic, extra);
            }
        }
    }

    /// <summary>The write went out: its bytes are spent. A write that failed spends nothing; the rest is given back when the request ends.</summary>
    public void CompleteWrite(long count)
    {
        if (count <= 0 || Closed) return;
        if (_exempt)
        {
            _guard.Ledger.AddOut(count);
            return;
        }
        lock (_guard.Gate)
        {
            if (Closed) return;
            var spend = Math.Min(count, _traffic);
            _guard.Ledger.SpendReserved(spend, inbound: false);
            _traffic -= spend;
            if (count > spend) _guard.Ledger.AddOut(count - spend);
        }
    }

    /// <summary>The headers of the answer arrived: their overhead was set aside at admission and is spent.</summary>
    public void CompleteResponseHeaders()
    {
        if (Closed) return;
        if (_exempt)
        {
            _guard.Ledger.AddIn(BlindMeteringTap.OverheadBytes);
            return;
        }
        lock (_guard.Gate)
        {
            if (Closed) return;
            var spend = Math.Min(BlindMeteringTap.OverheadBytes, _traffic);
            _guard.Ledger.SpendReserved(spend, inbound: true);
            _traffic -= spend;
            if (spend < BlindMeteringTap.OverheadBytes) _guard.Ledger.AddIn(BlindMeteringTap.OverheadBytes - spend);
        }
    }

    /// <summary>
    /// The answer announces <paramref name="declaredLength"/> bytes: when a storage limit applies, they are set aside before the first byte (a pause is thrown when they cannot fit, and an absurd
    /// length cannot fit). No announced length (null), or a zero one, sets nothing aside: what then arrives is charged as it is read, only as far as there is room.
    /// </summary>
    public void ReserveResponseStorage(long? declaredLength)
    {
        ThrowIfClosed();
        if (_exempt || declaredLength is not > 0 || !_guard.StorageLimited) return;
        _guard.PrepareMeasurements();
        lock (_guard.Gate)
        {
            ThrowIfClosed();
            if (_guard.StoragePauseLocked(declaredLength.Value) is { } pause) throw new BlindPausedException(pause);
            _guard.Stored.Reserve(declaredLength.Value);
            _storage = BlindMath.AddSat(_storage, declaredLength.Value);
        }
    }

    /// <summary>
    /// How many of the <paramref name="requested"/> bytes of the answer may be read now, decided and set aside in one step: never past the next 256 KB check, never more than the day's traffic
    /// that is left for the account and for the device, never more than the storage that is covered or left. Throws the pause when none may be read.
    /// </summary>
    public int BeginRead(int requested)
    {
        ThrowIfClosed();
        if (requested <= 0) return requested;
        if (_exempt) return requested;

        var atBoundary = _sinceCheck >= BlindMeteringTap.CheckEveryBytes;
        if (atBoundary) _guard.PrepareMeasurements();
        lock (_guard.Gate)
        {
            ThrowIfClosed();
            if (atBoundary)
            {
                // The full decision, every 256 KB: the person's switches, the day, a limit changed in the meantime.
                if (_guard.EvaluateLocked() is { } pause) throw new BlindPausedException(pause);
                _sinceCheck = 0;
            }

            var allowed = Math.Min((long)requested, BlindMeteringTap.CheckEveryBytes - _sinceCheck);
            allowed = Math.Min(allowed, _guard.TrafficRoomLocked());

            _readLimited = _guard.StorageLimited;
            long extraStorage = 0;
            if (_readLimited)
            {
                var room = _guard.StorageRoomLocked(out var unknown);
                if (room is null) throw new BlindPausedException(unknown!);
                var coverage = BlindMath.AddSat(_storage, room.Value);
                allowed = Math.Min(allowed, coverage);
                extraStorage = BlindMath.SubFloor(allowed, _storage);
            }

            if (allowed <= 0)
            {
                // No room: the full decision names why (traffic reached, storage reached, a switch turned); a fallback keeps the stop typed if none of them does.
                throw new BlindPausedException(_guard.EvaluateLocked() ?? new BlindPause(BlindPauseReasons.Traffic, null, null));
            }

            _guard.Ledger.ReserveTraffic(allowed);
            _readTraffic = allowed;
            if (extraStorage > 0) _guard.Stored.Reserve(extraStorage);
            _readExtraStorage = extraStorage;
            return (int)allowed;
        }
    }

    /// <summary>The read returned <paramref name="read"/> bytes: they are spent from what was set aside, and the rest is given back.</summary>
    public void EndRead(int read)
    {
        if (Closed) return;
        if (_exempt)
        {
            if (read > 0) _guard.Ledger.AddIn(read);
            return;
        }
        lock (_guard.Gate)
        {
            if (Closed) return;
            _guard.Ledger.SpendReserved(read, inbound: true);
            _guard.Ledger.UnreserveTraffic(BlindMath.SubFloor(_readTraffic, read));
            _readTraffic = 0;

            if (_readLimited)
            {
                var fromCover = Math.Min((long)read, _storage);
                _guard.Stored.ConvertReserved(fromCover);
                _storage -= fromCover;
                var fromExtra = Math.Min(BlindMath.SubFloor(read, fromCover), _readExtraStorage);
                _guard.Stored.ConvertReserved(fromExtra);
                _guard.Stored.Unreserve(BlindMath.SubFloor(_readExtraStorage, fromExtra));
            }
            else
            {
                _guard.Stored.Charge(read);
            }
            _readExtraStorage = 0;
            _sinceCheck += read;
        }
    }

    /// <summary>The read failed: what was set aside for it is given back.</summary>
    public void AbortRead()
    {
        if (_exempt || Closed) return;
        lock (_guard.Gate)
        {
            if (Closed) return;
            _guard.Ledger.UnreserveTraffic(_readTraffic);
            _guard.Stored.Unreserve(_readExtraStorage);
            _readTraffic = 0;
            _readExtraStorage = 0;
        }
    }

    /// <summary>Gives back what was set aside and not used. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        GC.SuppressFinalize(this);
        if (_exempt) return;
        lock (_guard.Gate)
        {
            _guard.Ledger.UnreserveTraffic(BlindMath.AddSat(_traffic, _readTraffic));
            _guard.Stored.Unreserve(BlindMath.AddSat(_storage, _readExtraStorage));
            _traffic = 0;
            _storage = 0;
            _readTraffic = 0;
            _readExtraStorage = 0;
        }
    }
}

/// <summary>How many times the size of a package archive a long job needs on disk at its peak (see <see cref="BlindLimitGuard.AdmitPackage"/>).</summary>
public static class BlindPackageFootprint
{
    /// <summary>A first load or a baseline update: the archive, its extraction, and the installed copy.</summary>
    public const int Install = 3;

    /// <summary>A backup: the archive, the extraction that checks it, the backup body and the encrypted file (the factor <c>BlindPhonePackageSource</c> asks the volume for).</summary>
    public const int Backup = 4;
}

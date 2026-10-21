using BeeMemoryBank.Api.Services.BlindBackup;

namespace BeeMemoryBank.Api.Services.BlindLimits;

/// <summary>
/// Why the node holds a request back, in the two words the answer and the log carry: the reason
/// token the full node reads (<c>X-BMB-Paused</c>) and the sentence a person gets. No number of the
/// owner travels with it — not the limit, not how much was used.
/// </summary>
public sealed record BlindPause(string Reason, TimeSpan RetryAfter, string Message)
{
    public const string Disk = "disk";
    public const string Traffic = "traffic";

    /// <summary>
    /// Not a limit reached: other requests in flight hold the room a limit leaves, and may give it back in a minute (<see cref="BusyRetryAfter"/>). The full node reads the token like
    /// any other reason of a pause and asks again soon; an older full node, which knows no token, sees an ordinary 503.
    /// </summary>
    public const string Busy = "busy";

    /// <summary>A storage pause has no end but a change of the setting; the answer offers the next hour as the next look.</summary>
    public static readonly TimeSpan DiskRetryAfter = TimeSpan.FromHours(1);

    /// <summary>When a busy node is asked again.</summary>
    public static readonly TimeSpan BusyRetryAfter = TimeSpan.FromMinutes(1);
}

/// <summary>
/// A seed that is arriving was held back by the node's storage limit while its parts were being received. It carries the pause the node answers with (503, <c>X-BMB-Paused: disk</c>
/// and a <c>Retry-After</c>, the same answer the door gives); what the seed staged has been removed by then.
/// </summary>
public sealed class BlindStoragePausedException(BlindPause pause) : InvalidOperationException("The seed was held back by the node's storage limit.")
{
    public BlindPause Pause { get; } = pause;
}

/// <summary>
/// A response announced a length that the day's traffic limit does not leave room for, and it had not started: the middleware answers with the pause instead of the response.
/// </summary>
public sealed class BlindResponseRefusedException(BlindPause pause) : InvalidOperationException("The response was held back by the node's traffic limit.")
{
    public BlindPause Pause { get; } = pause;
}

/// <summary>
/// What a seed that is arriving may still stage under the storage limit: the room is the limit less what the node holds without this seed's own staging (a fresh measurement,
/// including the other areas peers make it write into). <see cref="Judge"/> says whether <c>held + staged</c> has reached the limit.
/// </summary>
public sealed record BlindStagingRoom(BlindPause? Pause, long? Remaining, BlindPause? Reached)
{
    /// <summary>No storage limit: any amount may be staged.</summary>
    public static readonly BlindStagingRoom Unlimited = new(null, null, null);

    /// <summary>The pause to answer with when <paramref name="stagedBytes"/> have been staged, or null while they still fit. "Reached" holds: the limit itself is already too much.</summary>
    public BlindPause? Judge(long stagedBytes) => Pause ?? (Remaining is { } room && stagedBytes >= room ? Reached : null);
}

/// <summary>
/// The one place that says "not today" for a server blind node's limits: <see cref="Check"/> reads
/// the limits from the settings file and the counters (so a console change takes effect at the
/// next request, as on the apps) and answers null (go on) or the <see cref="BlindPause"/> the
/// middleware answers with. Traffic is judged before storage, the apps' order.
///
/// <para><b>The door looks, the handler reserves.</b> <see cref="Admit"/> is what the middleware asks
/// before the handler runs, and it only LOOKS: the limits are judged against what the day has counted
/// and what requests in flight have reserved, and a request that cannot go on is refused with the
/// pause. Nothing is reserved yet, because at the door nobody is known: the door runs before the
/// peer's token (or the seeder's proof, or the restore code) is checked, and a reservation taken
/// there is a budget lock for anyone who can reach the node. The handler calls
/// <see cref="BlindAdmission.Authenticated"/> (<see cref="BlindAdmissionExtensions.ReserveForPeer"/>)
/// right after it has authenticated the caller: THEN the bytes the request is planned to move - its
/// announced body length, or <see cref="UnknownSizeReservation"/> when it announced none - are
/// reserved, in one locked step, so that N authenticated requests arriving together cannot all pass
/// against the same remaining budget. A request that never authenticates holds nothing; what it
/// really moves is counted when it ends, and is limited by the bandwidth of whoever sends it.</para>
///
/// <para>The reservation follows the request: a response that announces its length is judged against
/// the room when it announces it (while it has not started: <see cref="BlindAdmission.Announce"/>),
/// and one that moves more than it reserved reserves what it has moved. It is given back, with what
/// really moved counted, when the request ends however it ends (<see cref="BlindAdmission.Dispose"/>:
/// an answer, an exception, a cancelled download, an aborted connection), and in any case
/// <see cref="ReservationLifetime"/> (plus the time its size needs at <see cref="LifetimeMinBytesPerSecond"/>) after it was taken the request is aborted: a body that trickles holds
/// its reservation for that long at most, and nothing it would still move is read or written. Reservations are in memory: they end with the process.</para>
///
/// <para><b>What can still be overshot, exactly.</b> A request that was admitted finishes (a
/// response is not cut off in the middle of its body, which the phones could not tell from a broken
/// node). So a cap can be passed by what the requests that were IN FLIGHT when it was reached
/// move beyond what was reserved for them: for a body or a response of announced length that is
/// nothing; for a body without an announced length, or a response that does not announce one, it is
/// what that request moves past <see cref="UnknownSizeReservation"/>. It is never more than the
/// bytes of the requests admitted before the cap was reached: nothing that arrives after is
/// admitted. A refusal that rests only on other requests' reservations (the day's own count would
/// leave room) is a short <see cref="BlindPause.Busy"/> answer, not "reached until midnight".</para>
///
/// <para>Fail closed: counts that cannot be read or saved, or a store whose size cannot be
/// measured, pause a node that has a limit that depends on them — each for the reason of that
/// limit. A node without limits of that kind is never stopped by them.</para>
/// </summary>
public sealed class BlindUsageGuard(
    BlindBackupSettingsStore settings,
    BlindUsageLedger ledger,
    BlindStoredSize stored,
    TimeProvider time)
{
    /// <summary>The clock the reservation lifetimes run on (a test steps it).</summary>
    internal TimeProvider Time => time;

    /// <summary>What a request reserves when it announced no length: a MiB. Documented in SECURITY.md and docs/blind-node/README.md.</summary>
    public const long UnknownSizeReservation = 1L << 20;

    /// <summary>
    /// How long an admitted request may take, as a base: half an hour, plus the time its size needs at <see cref="LifetimeMinBytesPerSecond"/> (<see cref="AllowedLifetime"/>). A request
    /// that has not finished by then is ABORTED - the connection is ended, the reservation is settled with what moved, and the peer retries as after any broken connection - so a body
    /// that trickles cannot hold the day's room, and nothing goes on past it, counted or not.
    /// </summary>
    public TimeSpan ReservationLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a request that was aborted at the end of its lifetime may take to unwind. If its handler is still not back after this (it is stuck in a call that takes no cancellation token),
    /// what it holds is SETTLED without it: the bytes it has moved are counted as used, the rest of its reservation is given back, and whatever it moves afterwards is counted straight into the day,
    /// never reserved again. The request is not forgotten: its reads and writes keep failing, and when the handler does return nothing is counted twice.
    /// </summary>
    public TimeSpan AbortGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The rate a transfer must average, over its whole size, not to be aborted at the end of its lifetime: 64 KiB/s. A big restore over a slow line still finishes; a drip does not.</summary>
    public long LifetimeMinBytesPerSecond { get; set; } = 64 * 1024;

    /// <summary>What a request that reserved <paramref name="bytes"/> is given to finish in: <see cref="ReservationLifetime"/> plus <paramref name="bytes"/> at <see cref="LifetimeMinBytesPerSecond"/>.</summary>
    internal TimeSpan AllowedLifetime(long bytes) =>
        ReservationLifetime + TimeSpan.FromSeconds(Math.Min(Math.Max(0, bytes) / (double)Math.Max(1, LifetimeMinBytesPerSecond), TimeSpan.FromDays(7).TotalSeconds));

    /// <summary>
    /// The pause in force, or null — a look, nothing is reserved. <paramref name="plannedBytes"/> is what the caller is about to
    /// add (an announced request body length); 0 when nothing is planned. What the requests in flight have reserved counts as used.
    /// </summary>
    public BlindPause? Check(long plannedBytes = 0)
    {
        var limits = settings.Load();
        plannedBytes = Math.Max(0, plannedBytes);
        if (limits.MaxTrafficPerDay is { } traffic)
        {
            if (!ledger.IsAvailable) return TrafficUnavailable();
            if (traffic == 0) return TrafficReached(traffic);
            // A request whose announced body is longer than the day that is left is refused before its first
            // byte is read — it would blow the budget by its own size, exactly as the apps refuse a load.
            if (PauseFor(ledger.Judge(plannedBytes, traffic, reserve: false), traffic, BlindPause.Traffic) is { } pause) return pause;
        }
        if (limits.MaxStoredBytes is { } disk)
        {
            if (disk == 0) return DiskZero();
            stored.EnsureFresh();
            var room = stored.Judge(plannedBytes, disk, reserve: false, out var known);
            if (!known) return DiskUnknown();
            if (PauseFor(room, disk, BlindPause.Disk) is { } pause) return pause;
        }
        return null;
    }

    /// <summary>
    /// The door: looks at a request that is about to be served and refuses it with the pause when a limit holds - nothing is reserved here (see the class summary). The result is
    /// disposed when the request ends - always, in a <c>finally</c> - and settles what it moved. When <see cref="BlindAdmission.Pause"/> is set the request is refused.
    /// </summary>
    /// <param name="contentLength">The announced length of the request body, or null.</param>
    /// <param name="writesStorage">The request carries a body the node will store (an event push, a blob): its planned size is judged against the storage limit and reserved against it once the caller is known.</param>
    /// <param name="storageIsTheHandlers">The handler judges the storage limit itself, with exact numbers (the seed, which stages in parts and removes its own staging when it must refuse): only traffic is judged here.</param>
    public BlindAdmission Admit(long? contentLength, bool writesStorage, bool storageIsTheHandlers = false)
    {
        var limits = settings.Load();
        var announced = Math.Max(0, contentLength ?? 0);

        long? trafficCap = null;
        if (limits.MaxTrafficPerDay is { } traffic)
        {
            if (!ledger.IsAvailable) return BlindAdmission.Refused(TrafficUnavailable());
            if (traffic == 0) return BlindAdmission.Refused(TrafficReached(traffic));
            if (PauseFor(ledger.Judge(announced, traffic, reserve: false), traffic, BlindPause.Traffic) is { } refusal) return BlindAdmission.Refused(refusal);
            trafficCap = traffic;
        }

        long? storageCap = null;
        if (limits.MaxStoredBytes is { } disk && !storageIsTheHandlers)
        {
            if (disk == 0) return BlindAdmission.Refused(DiskZero());
            if (writesStorage)
            {
                stored.EnsureFresh();
                var room = stored.Judge(contentLength is { } length ? Math.Max(0, length) : 0, disk, reserve: false, out var known);
                if (!known) return BlindAdmission.Refused(DiskUnknown());
                if (PauseFor(room, disk, BlindPause.Disk) is { } refusal) return BlindAdmission.Refused(refusal);
                storageCap = disk;
            }
            else
            {
                stored.EnsureFresh();
                var room = stored.Judge(0, disk, reserve: false, out var known);
                if (!known) return BlindAdmission.Refused(DiskUnknown());
                if (PauseFor(room, disk, BlindPause.Disk) is { } refusal) return BlindAdmission.Refused(refusal);
            }
        }
        return new BlindAdmission(this, trafficCap, storageCap, contentLength, announced);
    }

    /// <summary>
    /// For a seed that is arriving in parts: the room under the storage limit, measured fresh without the seed's own staging <paramref name="ownStagingDirectory"/> (the caller
    /// counts that exactly, as the bytes it has staged). Also what the node holds for the writes in flight. No storage limit: <see cref="BlindStagingRoom.Unlimited"/>.
    /// </summary>
    public BlindStagingRoom StagingRoom(string ownStagingDirectory)
    {
        if (settings.Load().MaxStoredBytes is not { } disk) return BlindStagingRoom.Unlimited;
        if (disk == 0) return new BlindStagingRoom(DiskZero(), 0, null);
        var reading = stored.MeasureExcluding(ownStagingDirectory);
        if (!reading.Known) return new BlindStagingRoom(DiskUnknown(), null, null);
        return new BlindStagingRoom(null, Math.Max(0, disk - Saturating(reading.Bytes, stored.Reserved)), DiskReachedPause());
    }

    /// <summary>
    /// Leaves a seed's working folders out of the stored-size walk the door's decisions rest on, for as long as the handle lives (see <see cref="BlindStoredSize.Exclude"/>); disposing
    /// it makes the next decision measure again.
    /// </summary>
    public IDisposable ExcludeFromDoor(params string[] directories)
    {
        var handles = directories.Select(stored.Exclude).ToList();
        return new Release(() =>
        {
            foreach (var handle in handles) handle.Dispose();
            stored.Invalidate();
        });
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }

    /// <summary>Counts <paramref name="bytes"/> that moved for the mesh (a request's and its response's bodies).</summary>
    public void CountTraffic(long bytes) => ledger.Add(bytes);

    /// <summary>Writes the traffic counts of the request that just ended (see <see cref="BlindUsageLedger.Flush"/>).</summary>
    public void FlushTraffic() => ledger.Flush();

    /// <summary>Why the traffic counts cannot be read or saved right now, in words without a path, or null when they can.</summary>
    public string? TrafficUnavailableReason() => ledger.UnavailableReason;

    /// <summary>What today moved (the status shows it); counting goes on whatever the limits say.</summary>
    public long TrafficToday() => ledger.UsageToday();

    /// <summary>When the node's local day turns over: the moment a reached daily traffic limit lifts.</summary>
    public DateTimeOffset NextLocalMidnight() => ledger.NextLocalMidnight();

    /// <summary>The stored-size reading the status shows; stale by at most the cache minute.</summary>
    public BlindStoredReading StoredNow()
    {
        stored.EnsureFresh();
        return stored.Peek();
    }

    /// <summary>What the pause is now (the status shows it), or null — the same decision the middleware asks. A node that is only busy with other transfers is not paused.</summary>
    public BlindPause? PauseNow() => Check(0) is { Reason: not BlindPause.Busy } pause ? pause : null;

    // ── what the admission reserves and settles with ──────────────────────

    internal BlindRoom ReserveTraffic(long bytes, long cap) => ledger.Judge(bytes, cap, reserve: true);

    internal void GrowTraffic(long bytes) => ledger.Grow(bytes);

    internal void SettleTraffic(bool limited, long reserved, long moved)
    {
        if (limited) ledger.Release(reserved, moved);
        else ledger.Add(moved);
    }

    internal BlindRoom ReserveStored(long bytes, long cap, out bool known) => stored.Judge(bytes, cap, reserve: true, out known);

    internal void SettleStored(long reserved, long written) => stored.Release(reserved, written);

    internal BlindPause? PauseOf(BlindRoom room, long cap, string reason) => PauseFor(room, cap, reason);

    internal BlindPause StorageUnknown() => DiskUnknown();

    // ── the words ─────────────────────────────────────────────────────────

    private BlindPause? PauseFor(BlindRoom room, long cap, string reason) => room switch
    {
        BlindRoom.Fits => null,
        BlindRoom.Busy => BusyPause(),
        _ => reason == BlindPause.Traffic ? TrafficReached(cap) : DiskReachedPause(),
    };

    private static BlindPause BusyPause() =>
        new(BlindPause.Busy, BlindPause.BusyRetryAfter, "This blind node is busy with other transfers right now. Ask again in a minute.");

    private static BlindPause TrafficUnavailable() =>
        new(BlindPause.Traffic, BlindPause.DiskRetryAfter, "This blind node is paused by its owner: its traffic counts cannot be read right now.");

    private BlindPause TrafficReached(long traffic)
    {
        var until = ledger.NextLocalMidnight() - time.GetUtcNow();
        var retry = traffic == 0 ? BlindPause.DiskRetryAfter
            : until > TimeSpan.Zero ? until : TimeSpan.FromMinutes(1);
        return new BlindPause(BlindPause.Traffic, retry,
            traffic == 0
                ? "This blind node is paused by its owner (the daily traffic limit is set to zero)."
                : "This blind node is paused by its owner: the daily traffic limit is reached. It answers again after the next local midnight.");
    }

    private static BlindPause DiskZero() =>
        new(BlindPause.Disk, BlindPause.DiskRetryAfter, "This blind node is paused by its owner (the storage limit is set to zero).");

    private static BlindPause DiskUnknown() =>
        new(BlindPause.Disk, BlindPause.DiskRetryAfter, "This blind node is paused by its owner: its stored size cannot be measured right now.");

    private static BlindPause DiskReachedPause() =>
        new(BlindPause.Disk, BlindPause.DiskRetryAfter,
            "This blind node is paused by its owner: the storage limit is reached. It answers again once the limit is raised or the node is wiped and seeded smaller.");

    private static long Saturating(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>
/// One request's place at the door (<see cref="BlindUsageGuard.Admit"/>): either a <see cref="Pause"/> that refuses it, or a place that holds NOTHING until the handler has
/// authenticated the caller and calls <see cref="Authenticated"/>; then the planned bytes are reserved. The middleware reports what moves (<see cref="Read"/>,
/// <see cref="Written"/>, <see cref="Announce"/>); <see cref="Dispose"/> settles, once: the reservation goes back and the bytes that really moved are counted. Safe to dispose
/// twice and from any thread.
/// </summary>
public sealed class BlindAdmission : IDisposable
{
    private readonly BlindUsageGuard? _guard;
    private readonly object _sync = new();
    private readonly long? _trafficCap;
    private readonly long? _storageCap;
    private readonly long? _contentLength;
    private readonly long _announcedRequest;
    private long _trafficReserved;
    private long _storedReserved;
    private long _read;
    private long _written;
    private bool _authenticated;
    private bool _announced;
    private bool _settled;
    private bool _expired;
    private bool _forced;
    private ITimer? _lifetime;
    private ITimer? _grace;
    private long _lifetimeFor;
    private DateTimeOffset _authenticatedAt;

    internal BlindAdmission(BlindUsageGuard guard, long? trafficCap, long? storageCap, long? contentLength, long announcedRequest)
    {
        _guard = guard;
        _trafficCap = trafficCap;
        _storageCap = storageCap;
        _contentLength = contentLength;
        _announcedRequest = announcedRequest;
    }

    private BlindAdmission(BlindPause pause) => Pause = pause;

    internal static BlindAdmission Refused(BlindPause pause) => new(pause);

    /// <summary>Set when the request is refused: the answer to give. Null when it was admitted.</summary>
    public BlindPause? Pause { get; }

    /// <summary>True once the handler has said who is asking (<see cref="Authenticated"/>): what the request is planned to move is reserved from then on.</summary>
    public bool IsAuthenticated
    {
        get
        {
            lock (_sync) return _authenticated;
        }
    }

    /// <summary>What ends the request when its lifetime is over (the middleware sets it to the request's abort). The request's streams refuse to move another byte from then on, whether this does anything or not.</summary>
    public Action? OnExpired { get; set; }

    /// <summary>
    /// The handler knows who is asking: reserve what the request is planned to move (its announced body, at least <see cref="BlindUsageGuard.UnknownSizeReservation"/>) against the
    /// daily traffic limit and, for a request that stores its body, against the storage limit. Null when it may go on (also when called again, or when no limit is set); the pause
    /// to answer with otherwise - the handler returns it (<see cref="BlindPauseResult"/>) before it does anything.
    /// </summary>
    public BlindPause? Authenticated()
    {
        if (_guard is null) return null;
        lock (_sync)
        {
            if (_settled || _authenticated) return null;
            long trafficReserved = 0;
            if (_trafficCap is { } cap)
            {
                trafficReserved = Math.Max(_announcedRequest, BlindUsageGuard.UnknownSizeReservation);
                if (_guard.PauseOf(_guard.ReserveTraffic(trafficReserved, cap), cap, BlindPause.Traffic) is { } refusal) return refusal;
            }
            long storedReserved = 0;
            if (_storageCap is { } disk)
            {
                storedReserved = _contentLength is { } length ? Math.Max(0, length) : BlindUsageGuard.UnknownSizeReservation;
                var room = _guard.ReserveStored(storedReserved, disk, out var known);
                if (room != BlindRoom.Fits)
                {
                    if (_trafficCap is not null) _guard.SettleTraffic(true, trafficReserved, 0);
                    return known ? _guard.PauseOf(room, disk, BlindPause.Disk) : _guard.StorageUnknown();
                }
            }
            _trafficReserved = trafficReserved;
            _storedReserved = storedReserved;
            _authenticated = true;
            StartLifetime();
            return null;
        }
    }

    /// <summary>Bytes of the request body that were read.</summary>
    public void Read(long bytes) => Moved(bytes, request: true);

    /// <summary>Bytes of the response body that were written.</summary>
    public void Written(long bytes) => Moved(bytes, request: false);

    private void Moved(long bytes, bool request)
    {
        if (_guard is null || bytes <= 0) return;
        bool expired;
        bool counted = false, storedLimited = false;
        lock (_sync)
        {
            if (_settled) return;
            if (request) _read += bytes;
            else _written += bytes;
            if (_forced)
            {
                // Settled without the handler (see Force): nothing is reserved any more, what moves now is counted into the day as it moves.
                counted = true;
                storedLimited = _storageCap is not null && _authenticated;
            }
            else if (_authenticated)
            {
                Reserve(Saturating(_read, _written));
            }
            expired = _expired;
        }
        if (counted)
        {
            _guard.SettleTraffic(false, 0, bytes);
            if (request && storedLimited) _guard.SettleStored(0, bytes);
        }
        // What moved is counted above; past the lifetime nothing more is handed over, so a request that ignores its abort still ends at its next read or write.
        if (expired) throw new BlindReservationExpiredException();
    }

    /// <summary>
    /// The response is about to be written and announced its length: judge the whole of it (the request's announced body and the response) against the daily limit. While the response
    /// has not started (<paramref name="mayRefuse"/>) a length that does not fit is refused with the pause; otherwise it is reserved as it is. A response of announced length is
    /// judged here whether or not the handler called <see cref="Authenticated"/> - it is producing a body, so it knows who asked.
    /// </summary>
    public BlindPause? Announce(long responseBytes, bool mayRefuse)
    {
        if (_guard is null || responseBytes <= 0) return null;
        lock (_sync)
        {
            if (_settled || _announced || _forced) return null;
            _announced = true;
            if (_trafficCap is not { } cap) return null;
            var target = Saturating(_announcedRequest, responseBytes);
            var grow = target - _trafficReserved;
            if (grow > 0)
            {
                if (mayRefuse)
                {
                    if (_guard.PauseOf(_guard.ReserveTraffic(grow, cap), cap, BlindPause.Traffic) is { } refusal) return refusal;
                }
                else
                {
                    _guard.GrowTraffic(grow);
                }
                _trafficReserved = target;
            }
            if (!_authenticated)
            {
                _authenticated = true;
                StartLifetime();
            }
            else
            {
                ArmLifetime();
            }
            return null;
        }
    }

    private void Reserve(long target)
    {
        if (_trafficCap is null || target <= _trafficReserved) return;
        _guard!.GrowTraffic(target - _trafficReserved);
        _trafficReserved = target;
        ArmLifetime();
    }

    private void StartLifetime()
    {
        _authenticatedAt = _guard!.Time.GetUtcNow();
        ArmLifetime();
    }

    /// <summary>
    /// A body that trickles must not hold its reservation for as long as it trickles: the request is given <see cref="BlindUsageGuard.AllowedLifetime"/> for what it reserves, counted from the
    /// moment it was authenticated, and the timer follows the reservation when it grows (a response that announces its length, a body that moves more than it reserved). EVERY request
    /// that holds something has one, the floor-sized included: ten idle requests that announced one byte each hold ten MiB of the cap for as long as their peer keeps the connections open.
    /// A request under no limit holds nothing and has none. Called under the lock.
    /// </summary>
    private void ArmLifetime()
    {
        if (_expired || (_trafficCap is null && _storageCap is null)) return;
        var bytes = Math.Max(_trafficReserved, _storedReserved);
        if (_lifetime is not null && bytes < _lifetimeFor + BlindUsageGuard.UnknownSizeReservation) return;
        _lifetimeFor = bytes;
        var left = _authenticatedAt + _guard!.AllowedLifetime(bytes) - _guard.Time.GetUtcNow();
        if (left < TimeSpan.FromMilliseconds(1)) left = TimeSpan.FromMilliseconds(1);
        if (_lifetime is null) _lifetime = _guard.Time.CreateTimer(static state => ((BlindAdmission)state!).Expire(), this, left, Timeout.InfiniteTimeSpan);
        else _lifetime.Change(left, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The request has outlived its lifetime: it is aborted. The reservation is NOT released here and the request stays metered - what it moves until it stops is still counted and still
    /// reserved - and it is settled in <see cref="Dispose"/>, when the request really ends. So nothing is ever unmetered, and the room is given back when the request is gone. A handler that
    /// does not come back within <see cref="BlindUsageGuard.AbortGrace"/> is settled without it (<see cref="Force"/>).
    /// </summary>
    private void Expire()
    {
        Action? end;
        lock (_sync)
        {
            if (_settled || _expired) return;
            _expired = true;
            end = OnExpired;
            _grace = _guard!.Time.CreateTimer(static state => ((BlindAdmission)state!).Force(), this, _guard.AbortGrace, Timeout.InfiniteTimeSpan);
        }
        try { end?.Invoke(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The request finished while its lifetime ran out: there is nothing left to abort.
        }
    }

    /// <summary>
    /// The handler did not return within the grace after the abort - it is stuck in a call that takes no cancellation token - and has held its reservation since: what it has moved is
    /// counted as used, the rest of its reservation (traffic and storage) is given back, and from now on what it moves is counted straight into the day as it moves, never reserved again
    /// (<see cref="Moved"/>). <see cref="Dispose"/>, when the handler does return, settles nothing more, so nothing is counted twice.
    /// </summary>
    private void Force()
    {
        long reserved, moved, read, storedReserved;
        bool storedLimited;
        lock (_sync)
        {
            if (_settled || _forced || !_expired) return;
            _forced = true;
            reserved = _trafficReserved;
            moved = Saturating(_read, _written);
            read = _read;
            storedReserved = _storedReserved;
            storedLimited = _storageCap is not null && _authenticated;
            _trafficReserved = 0;
            _storedReserved = 0;
        }
        _guard!.SettleTraffic(_trafficCap is not null, reserved, moved);
        if (storedLimited) _guard.SettleStored(storedReserved, read);
    }

    public void Dispose()
    {
        long reserved, moved, read, storedReserved;
        bool storedLimited;
        lock (_sync)
        {
            if (_settled || _guard is null) return;
            _settled = true;
            _lifetime?.Dispose();
            _grace?.Dispose();
            if (_forced) return;   // settled by Force; what moved since was counted as it moved
            reserved = _trafficReserved;
            moved = Saturating(_read, _written);
            read = _read;
            storedReserved = _storedReserved;
            storedLimited = _storageCap is not null && _authenticated;
        }
        _guard.SettleTraffic(_trafficCap is not null, reserved, moved);
        if (storedLimited) _guard.SettleStored(storedReserved, read);
    }

    private static long Saturating(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>
/// A request's lifetime is over (<see cref="BlindUsageGuard.ReservationLifetime"/>): the read or write that comes after it fails like a broken connection, so a handler that does not look at
/// its abort token still ends. The bytes that moved are already counted.
/// </summary>
public sealed class BlindReservationExpiredException()
    : IOException("The request took longer than the node allows a transfer of its size and was ended. Ask again.");

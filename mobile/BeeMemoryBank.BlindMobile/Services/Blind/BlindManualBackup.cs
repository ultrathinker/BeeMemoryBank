namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What <see cref="BlindManualBackup.Reserve"/> answered: the ticket of the account's request, or the account that holds the phone's one manual backup.</summary>
/// <param name="Accepted">True when the account holds the reservation after the call (it took it, or joined the run it already has).</param>
/// <param name="Generation">The generation of the reservation the request belongs to (what <see cref="BlindManualBackup.Withdraw"/> names).</param>
/// <param name="Holder">When refused: the account whose backup holds the phone.</param>
/// <param name="Stopping">When refused: the holder's service was destroyed and its backup is still ending; the person is told to ask again in a moment.</param>
public readonly record struct BlindBackupReservation(bool Accepted, long Generation, string? Holder, bool Stopping);

/// <summary>What a delivered start came to, see <see cref="BlindManualBackup.Deliver"/>.</summary>
public enum BlindDeliveryKind
{
    /// <summary>The start belongs to the CURRENT pending reservation: the caller owns the run now and must start the work, and call <see cref="BlindManualBackup.End"/> when it is over (or when starting it failed).</summary>
    Started,

    /// <summary>The caller's own run of this account is already going: the start only joins it (nothing to start, nothing to release).</summary>
    Joined,

    /// <summary>Nobody reserved this account any more (its reservation lapsed and another account took the phone, or none was made): nothing is started and the state is not touched.</summary>
    Stale,

    /// <summary>Somebody else's run holds the phone (another account, or an older service instance that has not ended): nothing is started and the state is not touched.</summary>
    Busy,
}

/// <summary>The answer of <see cref="BlindManualBackup.Deliver"/>: what to do, the generation of the run it started, and whose run holds the phone (to name it in the notification).</summary>
public readonly record struct BlindDelivery(BlindDeliveryKind Kind, long Generation, string? Holder);

/// <summary>
/// WHO may run the phone's one manual backup: ONE small state machine for the whole process, under one lock, and every decision about it. The platform service only reports what happened to it (a request, a delivered
/// start, an end, a destruction) and does what the answer says.
/// <code>
/// Idle  --Reserve(a)-->  Pending(a, generation, deadline)  --Deliver(a, owner)-->  Running(a, generation, owner)  --End(a, generation, owner)-->  Idle
/// </code>
/// <list type="bullet">
/// <item><b>Reserve</b> (the page's tap, synchronously, before the system is asked to start the service): from Idle it makes a Pending reservation; the same account joins it (or the run it already has); ANOTHER account is
///   refused on the spot with the holder's name. A Pending reservation is overtaken only when it is past its deadline AND another account reserves: a lost start cannot lock everyone out for ever, and a late start that
///   nobody has overtaken still runs. A Running one never lapses.</item>
/// <item><b>Deliver</b> (the system delivered the start to a service instance, its <c>owner</c> token): starts the work ONLY for the current Pending reservation of that account (-> Running, owned by that instance). Anything
///   else - nobody reserved it, another account holds it, an older instance's run has not ended - does NOTHING and says so (<see cref="BlindDeliveryKind"/>); it never touches the state, and a stale or duplicate delivery
///   cannot occupy anything.</item>
/// <item><b>End</b> releases the run only when the generation AND the owner are the ones that run was started with: an old end can never release a newer owner's generation, and two runs of one account cannot coexist.</item>
/// <item><b>Stopping</b> (the owner was destroyed): its run is on its way out; every request is refused with the explicit "stopping" answer until it ends, and a new instance's delivery finds Busy.</item>
/// <item><b>Withdraw</b>: a request whose start the system refused takes back its own request.</item>
/// </list>
/// The state is memory only: a killed process starts Idle.
/// </summary>
public sealed class BlindManualBackup(TimeProvider? time = null)
{
    /// <summary>How long a Pending reservation is kept against another account's request.</summary>
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(30);

    private enum Phase { Idle, Pending, Running }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private Phase _phase = Phase.Idle;
    private string? _account;
    private long _generation;
    private DateTimeOffset _deadline;
    private int _requests;
    private object? _owner;
    private bool _stopping;

    /// <summary>The account that holds the phone (a reservation within its deadline, or a run), or null.</summary>
    public string? Holder
    {
        get
        {
            lock (_gate)
                return _phase switch
                {
                    Phase.Running => _account,
                    Phase.Pending when _time.GetUtcNow() <= _deadline => _account,
                    _ => null,
                };
        }
    }

    /// <summary>True while an account's run holds the phone (not a mere reservation).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _phase == Phase.Running; }
    }

    public BlindBackupReservation Reserve(string accountId)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_phase == Phase.Running)
            {
                // The same account joins its run (unless that run is on its way out); anyone else is refused.
                if (!_stopping && Same(_account, accountId)) return new(true, _generation, _account, false);
                return new(false, _generation, _account, _stopping);
            }
            if (_phase == Phase.Pending)
            {
                if (Same(_account, accountId))
                {
                    _requests++;
                    _deadline = now + PendingTimeout;
                    return new(true, _generation, _account, false);
                }
                if (now <= _deadline) return new(false, _generation, _account, false);
                // Past its deadline and somebody else asks: the lost start is overtaken (its late delivery will find Stale).
            }
            _phase = Phase.Pending;
            _account = accountId;
            _generation++;
            _deadline = now + PendingTimeout;
            _requests = 1;
            _owner = null;
            _stopping = false;
            return new(true, _generation, _account, false);
        }
    }

    /// <summary>A request whose start the system refused takes back its own request (a Pending reservation goes when no other request of the account still waits for its start; a run is never touched).</summary>
    public void Withdraw(string accountId, long generation)
    {
        lock (_gate)
        {
            if (_phase != Phase.Pending || _generation != generation || !Same(_account, accountId)) return;
            if (--_requests <= 0) ToIdle();
        }
    }

    /// <summary>A start reached a service instance (<paramref name="owner"/> is that instance's token). See <see cref="BlindDeliveryKind"/>.</summary>
    public BlindDelivery Deliver(string? accountId, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (accountId is null) return new(BlindDeliveryKind.Stale, 0, HolderNow());
            switch (_phase)
            {
                case Phase.Pending when Same(_account, accountId):
                    _phase = Phase.Running;
                    _owner = owner;
                    _stopping = false;
                    _requests = 0;
                    return new(BlindDeliveryKind.Started, _generation, _account);
                case Phase.Pending:
                    return new(BlindDeliveryKind.Stale, 0, HolderNow());
                case Phase.Running when !_stopping && Same(_account, accountId) && ReferenceEquals(_owner, owner):
                    return new(BlindDeliveryKind.Joined, _generation, _account);
                case Phase.Running:
                    return new(BlindDeliveryKind.Busy, 0, _account);
                default:
                    return new(BlindDeliveryKind.Stale, 0, null);
            }
        }
    }

    /// <summary>The run ended (or could not be started): releases it, but ONLY when <paramref name="generation"/> and <paramref name="owner"/> are the ones it was started with. True when it was released.</summary>
    public bool End(string accountId, long generation, object owner)
    {
        lock (_gate)
        {
            if (_phase != Phase.Running || _generation != generation || !Same(_account, accountId) || !ReferenceEquals(_owner, owner)) return false;
            ToIdle();
            return true;
        }
    }

    /// <summary>The owner (a service instance) was destroyed: the run it started is on its way out. Requests are refused with "stopping" until it ends.</summary>
    public void Stopping(object owner)
    {
        lock (_gate)
            if (_phase == Phase.Running && ReferenceEquals(_owner, owner)) _stopping = true;
    }

    private string? HolderNow() => _phase == Phase.Running || _phase == Phase.Pending && _time.GetUtcNow() <= _deadline ? _account : null;

    private void ToIdle()
    {
        _phase = Phase.Idle;
        _account = null;
        _owner = null;
        _stopping = false;
        _requests = 0;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>What the page says when its request was refused because another account's backup holds the phone. The name is the account's own (the person gave it); null says "another copy".</summary>
    public static string RefusalText(string? holderName, bool stopping = false)
    {
        var who = string.IsNullOrWhiteSpace(holderName) ? "another copy" : BlindNotificationText.NameOf(holderName);
        return stopping
            ? $"The backup of {who} is stopping. Ask again in a moment."
            : $"The backup of {who} is running. This phone makes one backup at a time: ask again when it has finished.";
    }
}

/// <summary>The platform calls the one service instance needs; the Android service implements them and nothing else is Android in <see cref="BlindManualBackupService"/>.</summary>
public interface IBlindBackupServiceHost
{
    /// <summary>Makes the service a foreground service with the notification of <paramref name="accountId"/> (null: the neutral words). May throw (the platform may refuse).</summary>
    void ShowForeground(string? accountId);

    void HideForeground();

    /// <summary>Stops the service when <paramref name="startId"/> is the latest start it was given (<c>stopSelf(startId)</c>).</summary>
    void StopAfter(int startId);

    /// <summary>The backup of one account (the work itself). Runs on a pool thread; honours <paramref name="ct"/>.</summary>
    Task RunAsync(string accountId, CancellationToken ct);
}

/// <summary>
/// One service instance's side of the manual backup, with every decision in portable code over the process-wide <see cref="BlindManualBackup"/>: what a delivered start means, when the work starts, what an end
/// releases, what a destruction does. The platform service creates one in <c>OnCreate</c> and forwards <c>OnStartCommand</c> and <c>OnDestroy</c>.
/// <para>A start that does not start work (stale, busy, joined) never occupies anything: it answers the system's rule that a started foreground service must call <c>startForeground</c> and, when this instance
/// has no run of its own, stops itself; it never stops the service under its own run. Anything that throws between accepting a run and starting its task (the foreground step, the thread pool) gives the run back
/// at once (<see cref="BlindManualBackup.End"/> with exactly the generation that was accepted), so a failed start cannot leave the phone held for ever.</para>
/// </summary>
public sealed class BlindManualBackupService(BlindManualBackup machine, IBlindBackupServiceHost host)
{
    private readonly object _owner = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _lastStartId;
    private bool _running;
    private bool _destroyed;

    /// <summary>The task of this instance's run, or null (tests wait on it).</summary>
    public Task? RunTask
    {
        get { lock (_gate) return _task; }
    }

    /// <summary>The token that says whose run this instance's is (a test names it to end a run the way an older instance's late end would).</summary>
    internal object Owner => _owner;

    public void OnStart(string? accountId, int startId)
    {
        lock (_gate)
        {
            _lastStartId = startId;
            var delivery = machine.Deliver(accountId, _owner);
            switch (delivery.Kind)
            {
                case BlindDeliveryKind.Started:
                    StartRun(accountId!, delivery.Generation, startId);
                    return;
                case BlindDeliveryKind.Joined:
                    // The system still wants the foreground call; a refusal of it changes nothing for the run that is going.
                    TryShowForeground(accountId);
                    return;
                default:
                    // Nothing started and nothing touched. Acknowledge the start, and let go of the service unless this instance's own run needs it (its end stops the service for the latest start id).
                    TryShowForeground(delivery.Holder);
                    if (!_running) Stop(startId);
                    return;
            }
        }
    }

    public void OnDestroy()
    {
        lock (_gate)
        {
            _destroyed = true;
            machine.Stopping(_owner);
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void StartRun(string accountId, long generation, int startId)
    {
        try
        {
            host.ShowForeground(accountId);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _running = true;
            _task = Task.Run(async () =>
            {
                try { await host.RunAsync(accountId, cts.Token).ConfigureAwait(false); }
                catch (Exception) { /* the work reports its own failures (the log); an escaped one must still end the run */ }
                finally { Ended(accountId, generation, cts); }
            });
        }
        catch (Exception)
        {
            // The foreground step or the thread pool failed AFTER the run was accepted: it is given back at once, with exactly the generation that was accepted.
            _running = false;
            _cts?.Dispose();
            _cts = null;
            machine.End(accountId, generation, _owner);
            Stop(startId);
        }
    }

    private void Ended(string accountId, long generation, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            // Only this run's own generation and owner are released: a late end can never free a newer owner's run.
            machine.End(accountId, generation, _owner);
            _running = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            if (!_destroyed) Stop(_lastStartId);
        }
    }

    private void TryShowForeground(string? accountId)
    {
        try { host.ShowForeground(accountId); }
        catch (Exception) { /* the platform refused; there is no run of this start to give back */ }
    }

    private void Stop(int startId)
    {
        try { host.HideForeground(); }
        catch (Exception) { }
        try { host.StopAfter(startId); }
        catch (Exception) { }
    }
}

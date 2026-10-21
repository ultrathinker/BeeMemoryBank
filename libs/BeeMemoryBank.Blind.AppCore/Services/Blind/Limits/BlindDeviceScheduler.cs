using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Timings of <see cref="BlindDeviceScheduler"/>; the defaults are the rhythm of the single-account copy (a round every 15 minutes, the long job every hour).</summary>
public sealed class BlindDeviceSchedulerOptions
{
    /// <summary>How often the loop looks at what is due.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A sync round of each account, on any network its switches allow.</summary>
    public TimeSpan SyncEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The long job (first load, due backup) of each account is considered this often.</summary>
    public TimeSpan HeavyEvery { get; init; } = TimeSpan.FromHours(1);

    /// <summary>After a first load that failed the next try comes after this, doubling up to <see cref="FirstLoadRetryMax"/>. A PAUSE is not a failed load and starts no such retry.</summary>
    public TimeSpan FirstLoadRetry { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan FirstLoadRetryMax { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an account whose pause has no known end (a storage limit, "paused by you", a limit of zero) is left alone; a change of its settings ends it sooner.</summary>
    public TimeSpan LastingPauseLook { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// After a long job found the device busy because another account's job was let go of (it did not stop), the next try comes after this, doubling up to <see cref="DeviceBusyRetryMax"/>. Not a
    /// failed load: no first-load back-off grows. The job of the account that holds the gate may end any time, and then the next try finds the gate free.
    /// </summary>
    public TimeSpan DeviceBusyRetry { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan DeviceBusyRetryMax { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long <see cref="BlindDeviceScheduler.StopAsync"/> waits for the loop to end.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one sync round of one account may run before it is asked to stop. A round that ends this way is a pause, not a failure; the other accounts go on.</summary>
    public TimeSpan SyncTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long one long job of one account (a first load, a baseline update, a backup) may run in one pass before it is asked to stop; what it downloaded is kept and the next pass goes on from there.</summary>
    public TimeSpan HeavyTimeout { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>How long a job that was asked to stop (its time ran out, or the scheduler is stopping) gets to do so. After that it is ABANDONED: the loop goes on to the next account and starts nothing new for this one until the old job has really ended.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether this scheduler runs sync rounds. A host whose platform gives sync rounds and long jobs separate jobs (Android: a short WorkManager worker and a foreground one) builds two schedulers over
    /// one account host, one with <see cref="RunHeavy"/> off and one with this off. Both on is the loop of a host that runs everything itself.
    /// </summary>
    public bool RunSync { get; init; } = true;

    /// <summary>Whether this scheduler runs long jobs (the first load, a due backup). See <see cref="RunSync"/>.</summary>
    public bool RunHeavy { get; init; } = true;
}

/// <summary>
/// ONE loop for the whole device (the several-account generalisation of the desktop's timer loop, as pure and testable code): it looks at every account in turn, ONE ACCOUNT AT A TIME,
/// the one that synced longest ago first (so a shared device budget goes to the copy that has waited longest), and runs at most one long job at a time (the device gate does the same
/// for jobs that a screen starts). For each account a sync round every 15 minutes, the long job every hour and at once after pairing or on request.
///
/// <para><b>A pause is not a failure.</b> An account that a limit, a switch or the person holds is not retried at once and does not grow the first-load back-off: it is skipped until its pause ends
/// (the next local midnight for a daily limit), or for a day when no end is known, or until its settings change; a pause for the network or the battery needs no skip (the next round is
/// 15 minutes away anyway and the switch may have turned by then). The round that discovers a pause also tells the source (<see cref="BlindPauseReporter"/>).</para>
/// </summary>
public sealed class BlindDeviceScheduler : IAsyncDisposable
{
    private readonly IBlindAccountsController _accounts;
    private readonly TimeProvider _time;
    private readonly BlindDeviceSchedulerOptions _options;
    private readonly Action<string>? _report;
    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly Dictionary<string, AccountPlan> _plans = new(StringComparer.Ordinal);
    private readonly object _planGate = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _subscribed;
    private TaskCompletionSource _wake = NewWake();
    private volatile string? _lastError;

    /// <param name="report">Where a problem of the scheduler itself goes (the blind app's log), in words without secrets.</param>
    public BlindDeviceScheduler(IBlindAccountsController accounts, TimeProvider? time = null, BlindDeviceSchedulerOptions? options = null, Action<string>? report = null)
    {
        _accounts = accounts;
        _time = time ?? TimeProvider.System;
        _options = options ?? new BlindDeviceSchedulerOptions();
        _report = report;
    }

    /// <summary>Raised with the account's id and a sentence when a job the person asked for ended. May come from any thread.</summary>
    public event Action<string, string>? UserJobReported;

    public bool IsRunning { get { lock (_lifecycle) return _loop is { IsCompleted: false }; } }

    /// <summary>The last unexpected error that stopped one pass of the loop (the loop itself goes on).</summary>
    public string? LastError => _lastError;

    /// <summary>What the scheduler plans for one account (tests and diagnostics read it): when it looks at it next, and until when it leaves it alone.</summary>
    public AccountSchedule? ScheduleOf(string accountId)
    {
        lock (_planGate)
            return _plans.TryGetValue(accountId, out var plan) ? new AccountSchedule(plan.NextSyncAt, plan.NextHeavyAt, plan.SkipUntil, plan.HeavyRetryAt, plan.RetryAttempts, plan.RetryDelay) : null;
    }

    // ---- the loop ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the loop. There is never more than one: while a loop is running, or is still draining after a stop that timed out, this does nothing; the loop that was stopped is replaced
    /// only after it has really ended.
    /// </summary>
    public void EnsureScheduled()
    {
        lock (_lifecycle)
        {
            if (_loop is { IsCompleted: false }) return;
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            if (!_subscribed)
            {
                _accounts.Changed += Wake;
                _subscribed = true;
            }
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>
    /// Asks the loop to end and waits for it up to <see cref="BlindDeviceSchedulerOptions.StopTimeout"/>. A loop that has not ended by then STAYS the scheduler's loop (it is still draining): no
    /// second loop can be started beside it, and a later stop waits for it again.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lifecycle)
        {
            cts = _loopCts;
            loop = _loop;
            if (_subscribed)
            {
                _accounts.Changed -= Wake;
                _subscribed = false;
            }
        }
        if (cts is null || loop is null) return;

        try { await cts.CancelAsync(); }
        catch (ObjectDisposedException) { }
        try
        {
            await loop.WaitAsync(_options.StopTimeout);
        }
        catch (TimeoutException)
        {
            return; // still draining: it keeps its place, and nothing replaces it until it is out
        }
        catch (OperationCanceledException)
        {
        }
        lock (_lifecycle)
        {
            if (!ReferenceEquals(_loop, loop)) return;
            _loop = null;
            _loopCts = null;
        }
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ---- requests ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>"Sync now" for one account (or all of them when null). Also ends a skip: the person asked.</summary>
    public void RequestSync(string? accountId = null)
    {
        lock (_planGate)
            foreach (var plan in Plans(accountId))
            {
                plan.SyncWanted = true;
                plan.SyncByUser = true;
                plan.SkipUntil = null;
            }
        Wake();
    }

    /// <summary>"Back up now": the first load if it is not done, then a backup whether or not one is due.</summary>
    public void RequestBackup(string accountId)
    {
        lock (_planGate)
            foreach (var plan in Plans(accountId))
            {
                plan.BackupRequested = true;
                plan.SkipUntil = null;
            }
        Wake();
    }

    /// <summary>Right after pairing: the first load (or a due backup) without waiting for the next hour.</summary>
    public void RequestHeavy(string accountId)
    {
        lock (_planGate)
            foreach (var plan in Plans(accountId)) plan.HeavyWanted = true;
        Wake();
    }

    private IEnumerable<AccountPlan> Plans(string? accountId)
    {
        if (accountId is not null) return [PlanFor(accountId)];
        return _plans.Values.ToList();
    }

    private AccountPlan PlanFor(string accountId)
    {
        if (!_plans.TryGetValue(accountId, out var plan)) _plans[accountId] = plan = new AccountPlan();
        return plan;
    }

    // ---- one pass ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One pass over every account, one after the other. Public so that a test (or a caller that wants "now") can drive it without the timer; a second call while a pass runs returns at
    /// once.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!await _tickGate.WaitAsync(0, ct)) return;
        try
        {
            var snapshot = _accounts.GetSnapshot();
            if (snapshot.RegistryProblem is not null) return;

            // Plans of accounts that are gone are forgotten.
            lock (_planGate)
                foreach (var gone in _plans.Keys.Except(snapshot.Accounts.Select(a => a.Entry.Id)).ToList()) _plans.Remove(gone);

            // Accounts still waiting for an answer to an invitation are asked now and then; the host keeps each one's own rhythm, so a pass costs nothing until one is due.
            try { await _accounts.PollInvitationsAsync(force: false, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (!IsFatal(ex)) { Problem("Asking about an invitation failed", ex); }

            var order = PassOrder(snapshot);

            foreach (var card in order)
            {
                ct.ThrowIfCancellationRequested();
                await RunAccountAsync(card, snapshot.Device, ct);
            }
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>
    /// The accounts a pass spends a round on, in the order it takes them: only those that are active and have started, the one that synced longest ago first (an account that never synced
    /// is the oldest), then by the order of the list. Public so that a host whose platform grants a short, unpredictable slice of time per wake (the iPhone's background tasks) takes the
    /// accounts in the very order of the in-app loop instead of keeping a rule of its own.
    /// </summary>
    public static IReadOnlyList<BlindAccountSnapshot> PassOrder(BlindAccountsSnapshot snapshot) =>
        snapshot.Accounts
            .Where(a => a.Entry.Phase == BlindAccountPhases.Active
                && a.Status is { StartError: null, BackupKeyLost: false, KeyStoreUnavailable: null })
            .OrderBy(a => a.Status!.LastSyncAt ?? DateTimeOffset.MinValue)
            .ThenBy(a => a.Entry.Order)
            .ToList();

    private async Task RunAccountAsync(BlindAccountSnapshot card, BlindDeviceLimits device, CancellationToken ct)
    {
        var id = card.Entry.Id;
        var now = _time.GetUtcNow();
        bool syncDue, heavyDue, byUser, backup;
        lock (_planGate)
        {
            var plan = PlanFor(id);

            // A job of this account that was abandoned (it would not stop) may still be running: nothing new is started for the account until it has really ended.
            if (plan.Abandoned is { IsCompleted: false }) return;
            plan.Abandoned = null;

            // A change of this account's settings (or of the device's limits) ends a skip: the person may have just made room for it.
            var key = new PolicyKey(card.Entry.EffectiveLimits, card.Entry.UnmeteredOnly, card.Entry.PauseOnPowerSaving, card.Entry.PausedByUser, device);
            if (plan.Policy is { } known && known != key) plan.SkipUntil = null;
            plan.Policy = key;

            if (plan.SkipUntil is { } until && now < until) return;
            plan.SkipUntil = null;

            plan.NextSyncAt ??= now;
            plan.NextHeavyAt ??= now;
            syncDue = _options.RunSync && (plan.SyncWanted || now >= plan.NextSyncAt);
            if (syncDue)
            {
                plan.NextSyncAt = now + _options.SyncEvery;
                byUser = plan.SyncByUser;
                plan.SyncWanted = false;
                plan.SyncByUser = false;
            }
            else byUser = false;

            if (_options.RunHeavy)
            {
                if (now >= plan.NextHeavyAt)
                {
                    plan.NextHeavyAt = now + _options.HeavyEvery;
                    plan.HeavyWanted = true;
                }
                if (plan.HeavyRetryAt is { } retryAt && now >= retryAt)
                {
                    plan.HeavyRetryAt = null;
                    plan.HeavyWanted = true;
                }
                heavyDue = plan.HeavyWanted || plan.BackupRequested;
                backup = plan.BackupRequested;
                plan.HeavyWanted = false;
                plan.BackupRequested = false;
            }
            else
            {
                // A scheduler that runs no long jobs leaves their requests and timers as they are.
                heavyDue = false;
                backup = false;
            }
        }

        IBlindAppController controller;
        try { controller = _accounts.For(id); }
        catch (KeyNotFoundException) { return; } // removed since the snapshot

        if (syncDue)
        {
            string? result;
            try
            {
                result = await BoundedAsync(id, "sync round", token => controller.RequestSyncAsync(token), _options.SyncTimeout, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = "Paused; it continues where it stopped."; // its time ran out and it stopped: a pause, not a failure
            }
            if (result is null) return; // it would not stop: abandoned, the other accounts go on
            if (byUser) ReportToUser(id, result);
            if (SkipIfPaused(id, now)) return;
        }

        // The card was taken before the sync round of this pass: if that round made the second refusal, the account is removed by its owner now and its long job is not run from the old card.
        if (heavyDue && card.Status is { IsPaired: true } && (!syncDue || IsStillActive(id)))
        {
            string? result;
            try
            {
                result = await BoundedAsync(id, "long job", token => controller.RunHeavyAsync(backup, token), _options.HeavyTimeout, ct, heavy: true);
                if (result is not null && backup) ReportToUser(id, result);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = "Paused; it continues where it stopped.";
                if (backup) ReportToUser(id, result);
            }
            if (result is null) return;

            // The device's gate is held by a job that was let go of: this one waits, on a wait of its own, and is not a failed load.
            if (result.StartsWith(BlindHeavyWork.DeviceBusyPrefix, StringComparison.Ordinal))
            {
                ScheduleDeviceBusyRetry(id, now);
                return;
            }
            ClearDeviceBusyRetry(id);

            if (!ct.IsCancellationRequested && !SkipIfPaused(id, now)) RetryFirstLoadSoonIfItFailed(id, controller, result);
        }
    }

    /// <summary>Whether the account is still an active one in the host's list now (not removed by its owner, not going away). A list that cannot be read says yes: the job's own checks decide.</summary>
    private bool IsStillActive(string accountId)
    {
        try { return _accounts.GetSnapshot().Accounts.Any(a => a.Entry.Id == accountId && a.Entry.Phase == BlindAccountPhases.Active); }
        catch (Exception ex) when (!IsFatal(ex)) { return true; }
    }

    /// <summary>
    /// Runs one job of one account under a token of its own (linked to the loop's, cancelled when <paramref name="timeout"/> runs out), and never waits for it longer than the timeout plus
    /// <see cref="BlindDeviceSchedulerOptions.StopGrace"/>. A job that honours the token ends with a result or an <see cref="OperationCanceledException"/> (the caller turns that into a pause).
    /// A job that does not is ABANDONED: null is returned, it is remembered as running, nothing new is started for its account until it ends, and the loop goes on to the next account.
    /// Cancellation of the loop itself is passed on (after the same abandonment of a job that will not stop).
    /// </summary>
    private async Task<string?> BoundedAsync(string accountId, string what, Func<CancellationToken, Task<string>> job, TimeSpan timeout, CancellationToken ct, bool heavy = false)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        Task<string> task;
        try
        {
            task = job(limit.Token);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            task = Task.FromException<string>(ex);
        }

        // Its own time, or the loop being stopped, whichever comes first; the job has been asked to stop either way (its token is linked to both) and then gets the grace to do so.
        await Task.WhenAny(task, Task.Delay(timeout, _time, ct)).ConfigureAwait(false);
        if (task.IsCompleted) return await task.ConfigureAwait(false);
        await Task.WhenAny(task, Task.Delay(_options.StopGrace, _time)).ConfigureAwait(false);
        if (task.IsCompleted) return await task.ConfigureAwait(false);

        // Not finished although it was asked to stop (or the loop is stopping): it is let go of, and remembered until it ends.
        lock (_planGate) PlanFor(accountId).Abandoned = task;
        _ = task.ContinueWith(ended => { _ = ended.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // The host records it (a long job holds the device's gate until it returns): the other accounts' long jobs then say why they wait, and the card of this one says its job did not stop.
        try { _accounts.NoteJobAbandoned(accountId, heavy); }
        catch (Exception ex) when (!IsFatal(ex)) { }
        ct.ThrowIfCancellationRequested();
        Problem($"A {what} did not stop when it was asked to; the other accounts go on and nothing new is started for it until it ends", new TimeoutException());
        return null;
    }

    /// <summary>
    /// Asks whether the round or job just made left the account paused; if so it is left alone until the pause ends. Not for the network or the battery (the next round is 15 minutes away
    /// and the switch may have turned by then), and not when nothing holds it.
    /// </summary>
    private bool SkipIfPaused(string id, DateTimeOffset now)
    {
        BlindPause? pause;
        try { pause = _accounts.PauseOf(id); }
        catch (KeyNotFoundException) { return true; }
        if (pause is null) return false;

        lock (_planGate)
        {
            var plan = PlanFor(id);
            // A pause is not a failure: no retry is planned, the back-off does not grow, and a load that was waiting for its retry waits for the pause instead.
            plan.HeavyRetryAt = null;
            plan.RetryAttempts = 0;
            plan.RetryDelay = null;
            // Not for a wait, nor for a fault of the device's own files: those can end by themselves (the switch turns, a locked file is released) or by a repair, and the next pass asks again.
            if (pause.Reason is not (BlindPauseReasons.MeteredNetwork or BlindPauseReasons.PowerSaving
                or BlindPauseReasons.UsageUnavailable or BlindPauseReasons.DeviceUsageUnavailable or BlindPauseReasons.StorageUnknown))
                plan.SkipUntil = pause.Until ?? now + _options.LastingPauseLook;
        }
        return true;
    }

    private void ScheduleDeviceBusyRetry(string id, DateTimeOffset now)
    {
        lock (_planGate)
        {
            var plan = PlanFor(id);
            plan.BusyAttempts++;
            plan.BusyDelay = plan.BusyDelay is { } last
                ? TimeSpan.FromTicks(Math.Min(last.Ticks * 2, _options.DeviceBusyRetryMax.Ticks))
                : _options.DeviceBusyRetry;
            plan.HeavyRetryAt = now + plan.BusyDelay;
        }
    }

    private void ClearDeviceBusyRetry(string id)
    {
        lock (_planGate)
        {
            var plan = PlanFor(id);
            plan.BusyAttempts = 0;
            plan.BusyDelay = null;
        }
    }

    /// <summary>The conditions were fine and the first load is still not done: it failed. Try again soon, with a growing pause, not at the next hour.</summary>
    private void RetryFirstLoadSoonIfItFailed(string id, IBlindAppController controller, string result)
    {
        BlindAppStatus status;
        try { status = controller.GetStatus(); }
        catch (Exception ex) when (!IsFatal(ex)) { return; }

        lock (_planGate)
        {
            var plan = PlanFor(id);
            if (!status.IsPaired || status.InitialLoadDone || result.StartsWith("Paused", StringComparison.Ordinal))
            {
                plan.RetryDelay = null;
                plan.HeavyRetryAt = null;
                plan.RetryAttempts = 0;
                return;
            }
            plan.RetryAttempts++;
            plan.RetryDelay = plan.RetryDelay is { } last
                ? TimeSpan.FromTicks(Math.Min(last.Ticks * 2, _options.FirstLoadRetryMax.Ticks))
                : _options.FirstLoadRetry;
            plan.HeavyRetryAt = _time.GetUtcNow() + plan.RetryDelay;
        }
    }

    private void ReportToUser(string accountId, string sentence)
    {
        try { UserJobReported?.Invoke(accountId, sentence); }
        catch (Exception ex) when (!IsFatal(ex)) { Problem("Showing a job result failed", ex); }
    }

    // ---- the loop's own housekeeping --------------------------------------------------------------------------------------------------------

    private void Problem(string what, Exception ex)
    {
        // The kind of failure and the type's name, never the message: this sentence is kept in LastError and handed to the device's log, and a message carries a path, a key alias or an address.
        var sentence = $"{what} ({BlindFailureText.WithoutFullStop(BlindFailureText.Typed(ex))}).";
        _lastError = sentence;
        try { _report?.Invoke(sentence); }
        catch (Exception reportFailure) when (!IsFatal(reportFailure)) { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                Problem("A pass of the scheduler failed", ex);
            }

            try
            {
                await WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                Problem("Waiting for the next pass failed", ex);
                try { await Task.Delay(_options.Tick, ct); }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                catch (Exception) { }
            }
        }
    }

    private async Task WaitForNextTickAsync(CancellationToken ct)
    {
        var wake = _wake.Task;
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(_options.Tick, _time, delay.Token);
        await Task.WhenAny(wake, timer);
        await delay.CancelAsync();
        ct.ThrowIfCancellationRequested();
        if (wake.IsCompleted) Interlocked.Exchange(ref _wake, NewWake());
    }

    private void Wake() => _wake.TrySetResult();

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or StackOverflowException or ThreadAbortException;

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- state ------------------------------------------------------------------------------------------------------------------------------

    private sealed class AccountPlan
    {
        public DateTimeOffset? NextSyncAt;
        public DateTimeOffset? NextHeavyAt;
        public DateTimeOffset? HeavyRetryAt;
        public DateTimeOffset? SkipUntil;
        public TimeSpan? RetryDelay;
        public int RetryAttempts;
        public bool SyncWanted;
        public bool SyncByUser;
        public bool HeavyWanted;
        public bool BackupRequested;
        public PolicyKey? Policy;
        public Task? Abandoned;
        public int BusyAttempts;
        public TimeSpan? BusyDelay;
    }

    private sealed record PolicyKey(BlindAccountLimits Limits, bool UnmeteredOnly, bool PauseOnPowerSaving, bool PausedByUser, BlindDeviceLimits Device);
}

/// <summary>What <see cref="BlindDeviceScheduler"/> plans for one account (a read-only copy).</summary>
/// <param name="SkipUntil">The account is left alone until then (a pause); null when nothing holds it.</param>
/// <param name="RetryDelay">The wait that led to <paramref name="HeavyRetryAt"/> after a failed first load (it doubles up to the ceiling); null when no retry is planned. A screen says "it tries every N minutes" from it.</param>
public sealed record AccountSchedule(DateTimeOffset? NextSyncAt, DateTimeOffset? NextHeavyAt, DateTimeOffset? SkipUntil, DateTimeOffset? HeavyRetryAt, int RetryAttempts, TimeSpan? RetryDelay = null);

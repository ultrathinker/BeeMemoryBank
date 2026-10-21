using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The three calls the app makes to the notification center of the phone (and the one question it asks once), as tasks. The iOS class gives them over <c>UNUserNotificationCenter</c> - each
/// task ends when the system has answered - and the tests give a fake that answers when and in the order the test wants.
/// </summary>
public interface ISilenceNotificationCenter
{
    /// <summary>The identifiers of the app's requests that are pending now.</summary>
    Task<IReadOnlyList<string>> PendingIdsAsync();

    /// <summary>Withdraws the pending requests with these identifiers; ends when the system has done it.</summary>
    Task RemoveAsync(IReadOnlyList<string> requestIds);

    /// <summary>Hands one request to the system, to be shown <paramref name="after"/> from now; the same identifier replaces the pending one. True when the system accepted it.</summary>
    Task<bool> AddAsync(SilenceAdd add, TimeSpan after);

    /// <summary>Asks the person for permission to show alerts (the system asks once; later calls are answered from its memory).</summary>
    void RequestPermission();
}

/// <summary>A queue of work that runs one piece after the other, in the order it was handed over. A piece never starts inline in the caller; one that fails does not stop the next.</summary>
public sealed class AsyncSerialQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task Enqueue(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            var next = RunAfterAsync(_tail, work);
            _tail = next;
            return next;
        }
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> work)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        await work().ConfigureAwait(false);
    }
}

/// <summary>
/// Keeps the notification center in step with the accounts, ONE operation at a time. The whole read / withdraw / add of an update - and the look at which requests have fired - is one job
/// of one ordered queue, started only after the previous job ended, so that a late withdrawal can never delete a newer add of the same request, an older read can never reconcile the center
/// from an older snapshot after a newer plan, and the book (<see cref="SilenceRequestBook"/>) believes about the center only what the center has confirmed:
/// <list type="bullet">
/// <item>a job plans from the NEWEST snapshot that was handed over when it starts (an update that arrives while one is in flight waits for it; several that wait are one job);</item>
/// <item>the answer to an add counts only for the newest generation of its request (the book re-checks after every asynchronous callback), and a failed or lost answer - the center is given
///   <see cref="NativeTimeout"/> to answer each call - invalidates the plan, so that the next update does it again;</item>
/// <item>the fired requests are looked for at the start of the job (at most once a minute), so that the reminder that fired is planned again in the same pass.</item>
/// </list>
/// Platform-free; the iOS class only gives it the center.
/// </summary>
public sealed class SilenceReconciler
{
    /// <summary>How often the center is asked which requests are still pending (to find the ones that fired).</summary>
    public static readonly TimeSpan FiredLookEvery = TimeSpan.FromSeconds(60);

    /// <summary>How long one call to the center may stay unanswered before the job goes on without it (a lost callback must not stop every later update).</summary>
    public static readonly TimeSpan DefaultNativeTimeout = TimeSpan.FromSeconds(30);

    private readonly ISilenceNotificationCenter _center;
    private readonly TimeProvider _time;
    private readonly AsyncSerialQueue _queue = new();
    private readonly object _gate = new();
    private BlindAccountsSnapshot? _latest;
    private Task? _queuedUpdate;
    private bool _cleaned;
    private bool _asked;
    private DateTimeOffset _firedLookAt = DateTimeOffset.MinValue;

    public SilenceReconciler(ISilenceNotificationCenter center, SilenceRequestBook? book = null, TimeProvider? time = null, TimeSpan? nativeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(center);
        _center = center;
        Book = book ?? new SilenceRequestBook();
        _time = time ?? TimeProvider.System;
        NativeTimeout = nativeTimeout ?? DefaultNativeTimeout;
    }

    public SilenceRequestBook Book { get; }

    public TimeSpan NativeTimeout { get; }

    /// <summary>
    /// Brings the center in step with this snapshot, after every earlier job. The task ends when this job (or the one that took over for it, when several wait) has ended; it never throws.
    /// </summary>
    public Task UpdateAsync(BlindAccountsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _latest = snapshot;
            return _queuedUpdate ??= _queue.Enqueue(RunUpdateAsync);
        }
    }

    /// <summary>
    /// Alerts were just allowed (the person said yes, or allowed them later in Settings). A job of the same queue, after every earlier one: the book forgets what it had planned when nothing is known
    /// to be pending, and the NEWEST snapshot that was handed over is reconciled at once - an add the system refused before the permission was granted was invalidated, and nothing but this would
    /// add it again until some later update happened to come. With no snapshot yet there is nothing to add; the first update does it.
    /// </summary>
    public Task AlertsAllowedAsync() => _queue.Enqueue(async () =>
    {
        Book.AlertsAllowed();
        BlindAccountsSnapshot? newest;
        lock (_gate) newest = _latest;
        if (newest is not null) await ReconcileAsync(newest).ConfigureAwait(false);
    });

    private async Task RunUpdateAsync()
    {
        BlindAccountsSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _latest!;
            _queuedUpdate = null;
        }
        await ReconcileAsync(snapshot).ConfigureAwait(false);
    }

    /// <summary>One reconciliation of the center with a snapshot: the fired requests, the stale ones, the plan. Runs inside the queue only; it never throws.</summary>
    private async Task ReconcileAsync(BlindAccountsSnapshot snapshot)
    {
        try
        {
            await LookForFiredIfDueAsync().ConfigureAwait(false);
            if (!_cleaned && snapshot.RegistryProblem is null) _cleaned = await WithdrawStaleAsync(snapshot).ConfigureAwait(false);

            var now = _time.GetUtcNow();
            var plan = Book.Plan(snapshot, now);
            if (plan.Removes.Count > 0) await RemoveAsync(plan.Removes).ConfigureAwait(false);
            foreach (var add in plan.Adds)
            {
                if (!_asked)
                {
                    _asked = true;
                    _center.RequestPermission();
                }
                var after = TimeSpan.FromSeconds(Math.Max(60, (add.Notice.FireAt - now).TotalSeconds));
                var accepted = false;
                try { accepted = await Native(() => _center.AddAsync(add, after)).ConfigureAwait(false); }
                catch (Exception) { /* a center that fails or does not answer has not accepted it */ }
                Book.Answered(add.RequestId, add.Generation, accepted, _time.GetUtcNow() + after);
            }
        }
        catch (Exception)
        {
            // A job never throws: the next update plans again from what the book still believes.
        }
    }

    private async Task RemoveAsync(IReadOnlyList<string> requestIds)
    {
        var done = false;
        try
        {
            await Native(async () =>
            {
                await _center.RemoveAsync(requestIds).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            done = true;
        }
        catch (Exception) { /* not confirmed */ }
        Book.RemoveAnswered(requestIds, done);
    }

    private async Task LookForFiredIfDueAsync()
    {
        var now = _time.GetUtcNow();
        if (now - _firedLookAt < FiredLookEvery) return;
        _firedLookAt = now;
        IReadOnlyList<string> pending;
        try { pending = await Native(() => _center.PendingIdsAsync()).ConfigureAwait(false); }
        catch (Exception) { return; }
        Book.NoteFired(pending, _time.GetUtcNow());
    }

    /// <summary>Withdraws what an account that was removed while the app was not running left pending. True when it was looked at (and done); false: the next job looks again.</summary>
    private async Task<bool> WithdrawStaleAsync(BlindAccountsSnapshot snapshot)
    {
        try
        {
            var pending = await Native(() => _center.PendingIdsAsync()).ConfigureAwait(false);
            var stale = BlindSilenceAlarm.StaleRequestIds(pending, snapshot.Accounts.Select(a => a.Entry));
            if (stale.Count > 0) await Native(async () =>
            {
                await _center.RemoveAsync(stale).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>One call to the center, given <see cref="NativeTimeout"/> to answer.</summary>
    private async Task<T> Native<T>(Func<Task<T>> call) => await call().WaitAsync(NativeTimeout, _time).ConfigureAwait(false);
}

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>What the lifecycle drives: the accounts, the in-app loop and nothing else. The runtime gives its host and scheduler; the tests give fakes.</summary>
public interface IIosLoop
{
    /// <summary>Takes the app's folder and starts every account (safe to call again).</summary>
    Task StartAccountsAsync(CancellationToken ct);

    /// <summary>Starts the in-app loop when it is not running.</summary>
    void EnsureLoop();

    /// <summary>Stops the in-app loop and waits until it has really stopped (a job that does not stop in time is let go of).</summary>
    Task StopLoopAsync();

    /// <summary>True while the loop has not ended.</summary>
    bool LoopRunning { get; }
}

/// <summary>
/// The whole life cycle of the iPhone app in ONE ordered queue: the app coming to the front, the app going to the background, a background round and the end of the process are
/// transitions that run one after the other, in the order they were asked for. A newer transition waits for the older one, so
/// <list type="bullet">
/// <item>a stop that finishes late can never stop the loop of a newer start - a start that follows a stop runs after it;</item>
/// <item>the loop and a background round are never inside an account together: a start waits for the round that is on, which it has asked to stop at once;</item>
/// <item>disposal waits for the transition in flight, cancels the round and stops the loop, and nothing starts after it.</item>
/// </list>
/// What the person wants (<see cref="InForeground"/>) is set the moment a transition is ASKED for, on the caller's thread, so the order of <c>OnSleep</c> and <c>OnResume</c> is the order of
/// the flag; each transition then makes the loop match what is wanted when its turn comes, not what was wanted when it was asked (a stale one has nothing left to undo). The final state is
/// always the last wish: foreground = the loop runs, background = the loop is stopped, never both and never neither.
/// </summary>
public sealed class IosLifecycle : IAsyncDisposable
{
    private const string AppOpen = "The app is open: it serves the accounts itself, this wake has nothing to do.";
    private const string AnotherRound = "Another background round of this device is on, this wake has nothing to do.";
    private const string Closing = "The app is closing, this wake has nothing to do.";

    private readonly IIosLoop _loop;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private CancellationTokenSource? _round;
    private bool _roundPending;
    private volatile bool _disposed;
    private volatile bool _foreground;

    public IosLifecycle(IIosLoop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
    }

    /// <summary>True from the moment the app was asked to come to the front until it was asked to leave: the open app serves the accounts itself and a background wake has nothing to do.</summary>
    public bool InForeground => _foreground;

    /// <summary>
    /// The app came to the front (start, resume). A background round that is on is asked to stop NOW; the accounts are started and the loop runs once that round and every older transition
    /// have ended. The returned task ends then; a caller that stops waiting (<paramref name="ct"/>) leaves the transition to finish, so the state is never half-applied.
    /// </summary>
    public Task EnterForegroundAsync(CancellationToken ct = default) => Transition(foreground: true, ct);

    /// <summary>The app goes to the background: the loop is stopped (after every older transition), and from now on a background wake may run.</summary>
    public Task EnterBackgroundAsync() => Transition(foreground: false, CancellationToken.None);

    private Task Transition(bool foreground, CancellationToken ct)
    {
        Task task;
        CancellationTokenSource? round;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _foreground = foreground;
            round = foreground ? _round : null;
            task = Enqueue(ConvergeAsync);
        }
        Cancel(round);
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    /// <summary>Makes the loop match what is wanted now. Runs inside the queue, one at a time.</summary>
    private async Task<bool> ConvergeAsync()
    {
        if (_foreground)
        {
            await _loop.StartAccountsAsync(CancellationToken.None).ConfigureAwait(false);
            if (_foreground && !_disposed) _loop.EnsureLoop();
        }
        else
        {
            await _loop.StopLoopAsync().ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// ONE background round at a time for the whole device, whichever task iOS woke the app for. A round that is asked for while another one is queued or on, while the app is in front, or once the
    /// app is closing does nothing and says so (<see cref="IosRoundOutcome.Skipped"/>; the task is completed as "nothing to do"). Otherwise the round takes its place in the queue: it begins
    /// when every older transition has ended (the loop is stopped by then), and the app coming to the front cancels it and is run after it.
    /// </summary>
    public Task<IosRoundOutcome> RunBackgroundAsync(Func<CancellationToken, Task<IosRoundOutcome>> round, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(round);
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(IosRoundOutcome.SkippedBecause(Closing));
            if (_foreground) return Task.FromResult(IosRoundOutcome.SkippedBecause(AppOpen));
            if (_roundPending) return Task.FromResult(IosRoundOutcome.SkippedBecause(AnotherRound));
            _roundPending = true;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _round = cts;
            return Enqueue(() => RunRoundAsync(cts, round, ct));
        }
    }

    private async Task<IosRoundOutcome> RunRoundAsync(CancellationTokenSource cts, Func<CancellationToken, Task<IosRoundOutcome>> round, CancellationToken wake)
    {
        try
        {
            // iOS ended the wake before it began anything: the caller tells it apart from "the app is open".
            wake.ThrowIfCancellationRequested();
            if (Stopped()) return IosRoundOutcome.SkippedBecause(_disposed ? Closing : AppOpen);
            // The older transitions have ended and the last of them stopped the loop; a loop that still runs here was started by something else and is not let to run beside the round.
            if (_loop.LoopRunning) await _loop.StopLoopAsync().ConfigureAwait(false);
            if (Stopped()) return IosRoundOutcome.SkippedBecause(_disposed ? Closing : AppOpen);
            if (_loop.LoopRunning) return IosRoundOutcome.SkippedBecause("The app's own loop is still stopping, this wake has nothing to do.");
            return await round(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _round = null;
                _roundPending = false;
            }
            cts.Dispose();
        }

        bool Stopped() => _foreground || _disposed || (cts.IsCancellationRequested && !wake.IsCancellationRequested);
    }

    /// <summary>
    /// The process ends. The round that is on is cancelled, every transition in flight or queued runs to its end, the loop is stopped last and nothing starts afterwards (a start asked for
    /// later does nothing). Safe to call twice.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task last;
        CancellationTokenSource? round;
        lock (_gate)
        {
            round = _round;
            if (!_disposed)
            {
                _disposed = true;
                _foreground = false;
                Enqueue(ConvergeAsync);
            }
            last = _tail;
        }
        Cancel(round);
        await last.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private Task<T> Enqueue<T>(Func<Task<T>> work)
    {
        var next = RunAfterAsync(_tail, work);
        _tail = next;
        return next;
    }

    /// <summary>Runs after <paramref name="previous"/> whatever became of it (its own caller sees its failure) and never inline: the queue is entered under a lock.</summary>
    private static async Task<T> RunAfterAsync<T>(Task previous, Func<Task<T>> work)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        return await work().ConfigureAwait(false);
    }

    private static void Cancel(CancellationTokenSource? round)
    {
        try { round?.Cancel(); }
        catch (ObjectDisposedException) { /* the round ended in the meantime */ }
    }
}

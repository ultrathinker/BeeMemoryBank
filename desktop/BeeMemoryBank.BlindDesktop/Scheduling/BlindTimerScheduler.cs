using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindDesktop.Scheduling;

/// <summary>Timings of <see cref="BlindTimerScheduler"/>; the defaults are the Android worker's rhythm.</summary>
public sealed class BlindSchedulerOptions
{
    /// <summary>How often the loop looks at what is due.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A sync round on any network (WorkManager's shortest period).</summary>
    public TimeSpan SyncEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The long job (first load, due backup) is considered every hour.</summary>
    public TimeSpan HeavyEvery { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// After a first load that failed (typically right after pairing, before the listening node has heard of this
    /// device: 401) the next try comes after this, doubling up to <see cref="FirstLoadRetryMax"/>, not only at the next hour.
    /// </summary>
    public TimeSpan FirstLoadRetry { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan FirstLoadRetryMax { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long <see cref="BlindTimerScheduler.StopAsync"/> waits for a running job to pause.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>What the window and the tray can ask of the scheduler. Each request is picked up by the loop, which runs one job at a time.</summary>
public interface IBlindWorkRequests
{
    /// <summary>"Sync now".</summary>
    void RequestSync();

    /// <summary>"Back up now": the first load if it is not done, then a backup whether or not one is due.</summary>
    void RequestBackup();

    /// <summary>Right after pairing: request the first load (or a due backup) without waiting for the next hour.</summary>
    void RequestHeavy();

    /// <summary>
    /// Raised with a sentence for the screen when a job the user asked for ("Sync now", "Back up now") ended.
    /// May come from any thread.
    /// </summary>
    event Action<string>? UserJobReported;

    /// <summary>When the first load fails with every condition fine, the next try: null when none is planned.</summary>
    FirstLoadRetryInfo? FirstLoadRetry { get; }
}

/// <summary>The planned retry of a failed first load, for the screen.</summary>
/// <param name="NextAt">When the next try is made.</param>
/// <param name="Every">The pause that led to it (it doubles up to the ceiling).</param>
/// <param name="Attempts">How many tries have failed in a row since the last success.</param>
/// <param name="AtCeiling">True when the pause has reached its ceiling: the app then tries every <paramref name="Every"/> until it works.</param>
public sealed record FirstLoadRetryInfo(DateTimeOffset NextAt, TimeSpan Every, int Attempts, bool AtCeiling);

/// <summary>
/// The desktop counterpart of the Android WorkManager jobs: an in-process timer loop in the tray process.
/// <list type="bullet">
/// <item>A check right at the start, then a sync round every 15 minutes on any network, and the long job (the first load, the
/// backups) every hour, plus at once after pairing or "Back up now".</item>
/// <item>One job at a time: the loop awaits each job before it looks at anything else; requests made meanwhile are remembered.</item>
/// </list>
/// </summary>
public sealed class BlindTimerScheduler : IBlindScheduler, IBlindWorkRequests, IAsyncDisposable
{
    private readonly IBlindAppController _app;
    private readonly TimeProvider _time;
    private readonly BlindSchedulerOptions _options;
    private readonly Action<string>? _report;
    private readonly object _reportGate = new();
    private string? _lastProblem;
    private DateTimeOffset _lastProblemAt;
    private int _retryAttempts;

    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private TaskCompletionSource _wake = NewWake();

    private DateTimeOffset? _nextSyncAt;
    private DateTimeOffset? _nextHeavyAt;
    private DateTimeOffset? _heavyRetryAt;
    private TimeSpan? _retryDelay;
    private volatile bool _syncWanted;
    private volatile bool _syncAskedByUser;
    private volatile bool _backupRequested;
    private volatile bool _heavyWanted;
    private volatile string? _lastResult;
    private volatile string? _lastError;

    /// <param name="report">Where a problem of the scheduler itself goes (the blind app's log), in words without secrets; the same problem is
    /// not repeated within fifteen minutes.</param>
    public BlindTimerScheduler(IBlindAppController app, TimeProvider? time = null,
        BlindSchedulerOptions? options = null, Action<string>? report = null)
    {
        _app = app;
        _time = time ?? TimeProvider.System;
        _report = report;
        _options = options ?? new BlindSchedulerOptions();
    }

    /// <inheritdoc />
    public event Action<string>? UserJobReported;

    /// <inheritdoc />
    public FirstLoadRetryInfo? FirstLoadRetry
    {
        get
        {
            var at = _heavyRetryAt;
            var every = _retryDelay;
            return at is { } nextAt && every is { } delay ? new FirstLoadRetryInfo(nextAt, delay, _retryAttempts, delay >= _options.FirstLoadRetryMax) : null;
        }
    }

    /// <summary>True while the loop runs.</summary>
    public bool IsRunning
    {
        get { lock (_lifecycle) return _loop is { IsCompleted: false }; }
    }

    /// <summary>The sentence the last job ended with.</summary>
    public string? LastResult => _lastResult;

    /// <summary>The last unexpected error that stopped one pass of the loop (the loop itself goes on).</summary>
    public string? LastError => _lastError;

    /// <summary>Starts the loop if it is not running; its first pass is the check at start.</summary>
    public void EnsureScheduled()
    {
        lock (_lifecycle)
        {
            if (_loop is { IsCompleted: false }) return;
            _nextSyncAt = null;
            _nextHeavyAt = null;
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>Stops the loop and pauses a running job; returns when it has stopped (or after <see cref="BlindSchedulerOptions.StopTimeout"/>).</summary>
    public void Cancel() => StopAsync().GetAwaiter().GetResult();

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lifecycle)
        {
            cts = _loopCts;
            loop = _loop;
            _loopCts = null;
            _loop = null;
        }
        if (cts is null) return;

        await cts.CancelAsync();
        try
        {
            if (loop is not null) await loop.WaitAsync(_options.StopTimeout);
        }
        catch (TimeoutException)
        {
            // A job that does not pause in time is left to finish on its own; the cancellation has been sent.
        }
        catch (OperationCanceledException)
        {
        }
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    public void RequestSync()
    {
        _syncAskedByUser = true;
        _syncWanted = true;
        Wake();
    }

    public void RequestBackup()
    {
        _backupRequested = true;
        Wake();
    }

    public void RequestHeavy()
    {
        _heavyWanted = true;
        Wake();
    }

    /// <summary>
    /// One pass of the loop: what was asked for and what is due, run one after the other. Public so a test (or a caller that wants
    /// "now") can drive it without waiting for the timer; a second call while a pass is running returns at once.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!await _tickGate.WaitAsync(0, ct)) return;
        try
        {
            var now = _time.GetUtcNow();
            _nextSyncAt ??= now;
            _nextHeavyAt ??= now;

            if (now >= _nextSyncAt)
            {
                _nextSyncAt = now + _options.SyncEvery;
                _syncWanted = true;
            }
            if (_syncWanted) await RunWantedSyncAsync(ct);

            if (now >= _nextHeavyAt)
            {
                _nextHeavyAt = now + _options.HeavyEvery;
                _heavyWanted = true;
            }
            if (_heavyRetryAt is { } retryAt && now >= retryAt)
            {
                _heavyRetryAt = null;
                _heavyWanted = true;
            }
            if (_backupRequested || _heavyWanted) await RunWantedHeavyAsync(ct);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task RunWantedSyncAsync(CancellationToken ct)
    {
        var byUser = _syncAskedByUser;
        _syncWanted = false;
        _syncAskedByUser = false;
        _lastResult = await _app.RequestSyncAsync(ct);
        if (byUser) ReportToUser(_lastResult);
    }

    private async Task RunWantedHeavyAsync(CancellationToken ct)
    {
        var status = _app.GetStatus();
        if (!status.IsPaired)
        {
            // Nothing to load or back up before pairing; "Back up now" before pairing is not remembered.
            _backupRequested = false;
            _heavyWanted = false;
            return;
        }
        var forceBackup = _backupRequested;
        _backupRequested = false;
        _heavyWanted = false;

        try
        {
            _lastResult = await _app.RunHeavyAsync(forceBackup, ct);
            if (forceBackup) ReportToUser(_lastResult);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lastResult = "Paused; it continues where it stopped.";
            if (forceBackup) ReportToUser(_lastResult);
        }

        if (!ct.IsCancellationRequested)
        {
            RetryFirstLoadSoonIfItFailed();
        }
    }

    /// <summary>The conditions were fine and the first load is still not done: it failed. Try again soon, with a growing pause, not at the next hour.</summary>
    private void RetryFirstLoadSoonIfItFailed()
    {
        var status = _app.GetStatus();
        if (!status.IsPaired || status.InitialLoadDone)
        {
            _retryDelay = null;
            _heavyRetryAt = null;
            _retryAttempts = 0;
            return;
        }
        _retryAttempts++;
        _retryDelay = _retryDelay is { } last
            ? TimeSpan.FromTicks(Math.Min(last.Ticks * 2, _options.FirstLoadRetryMax.Ticks))
            : _options.FirstLoadRetry;
        _heavyRetryAt = _time.GetUtcNow() + _retryDelay;
    }

    /// <summary>A handler of the user-report event must never break the job or the loop that raised it.</summary>
    private void ReportToUser(string sentence)
    {
        try
        {
            UserJobReported?.Invoke(sentence);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Problem("Showing a job result failed", ex);
        }
    }

    /// <summary>
    /// A problem of the scheduler itself: kept in <see cref="LastError"/> and, in words without secrets (type and a short message), written
    /// once to the log - the same sentence again only after fifteen minutes.
    /// </summary>
    private void Problem(string what, Exception ex)
    {
        var message = (ex.Message ?? "").ReplaceLineEndings(" ").Trim();
        if (message.Length > 120) message = message[..120] + "...";
        var sentence = $"{what} ({ex.GetType().Name}: {message}).";
        _lastError = sentence;
        if (_report is null) return;
        lock (_reportGate)
        {
            var now = _time.GetUtcNow();
            if (sentence == _lastProblem && now - _lastProblemAt < TimeSpan.FromMinutes(15)) return;
            _lastProblem = sentence;
            _lastProblemAt = now;
        }
        try { _report(sentence); }
        catch (Exception reportFailure) when (!IsFatal(reportFailure)) { }
    }

    /// <summary>The loop runs on a pool thread and has no caller to hear an exception: it never ends because of one.</summary>
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
                // Jobs report their own failures to the log through the controller; this is a failure of the pass itself.
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
        // A request from now on completes a new source, so none is lost between two waits (it also set its flag first).
        if (wake.IsCompleted) Interlocked.Exchange(ref _wake, NewWake());
    }

    private void Wake() => _wake.TrySetResult();

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or StackOverflowException or ThreadAbortException;

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

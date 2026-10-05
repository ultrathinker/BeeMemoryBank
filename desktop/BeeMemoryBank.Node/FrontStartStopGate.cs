namespace BeeMemoryBank.Node;

/// <summary>
/// Keeps the front's start and its stop from ever running at the same time.
///
/// <para>Why it exists: stopping a Kestrel host while its <c>StartAsync</c> is still running disposes the
/// server's heartbeat under the heartbeat thread, which throws <see cref="ObjectDisposedException"/> on a
/// thread nobody can catch it on - the runtime then aborts the whole process (exit 134 on macOS) and the
/// node's status files are never cleaned up. A stop (stdin EOF, SIGTERM, Ctrl+C, the service stop token, a
/// critical failure) can arrive at any moment, including the few milliseconds right after launch.</para>
///
/// <para>The rules: a start is only allowed to begin while no stop has been requested; a stop waits (bounded)
/// for the start in flight to finish - failing or being canceled counts as finished, and the stop never sees
/// the start's exception - and only then stops the app that came out of it. If a start does not finish within
/// the bound, the stop gives up on the front instead of stopping it under its start, and the start call
/// returns <c>null</c> so the caller's run wait can complete.</para>
///
/// <para>Every start site goes through <see cref="StartAsync"/>, retries after a port failure included: the
/// gate builds the app, starts it, and disposes it itself when the start throws, so a failed app is never
/// left for the stop path to touch.</para>
/// </summary>
public sealed class FrontStartStopGate<TApp> where TApp : class, IAsyncDisposable
{
    /// <summary>How long a stop waits for a start in flight before it gives up on the front.</summary>
    public static readonly TimeSpan DefaultStartWaitBound = TimeSpan.FromSeconds(10);

    private readonly Func<TApp, Task> _stopAppAsync;
    private readonly TimeSpan _startWaitBound;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private readonly TaskCompletionSource _abandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _stopRequested;
    private int _startsInFlight;
    private TaskCompletionSource? _idle;   // completed when the last start in flight has finished
    private TApp? _running;                // the app whose start succeeded and has not been stopped yet

    /// <param name="stopAppAsync">Stops one started app. Called at most once per app, never while its start runs.</param>
    /// <param name="startWaitBound">How long <see cref="StopAsync"/> waits for a start in flight.</param>
    /// <param name="log">Where the one warning of an abandoned wait goes; may be null.</param>
    public FrontStartStopGate(Func<TApp, Task> stopAppAsync, TimeSpan startWaitBound, Action<string>? log = null)
    {
        _stopAppAsync = stopAppAsync ?? throw new ArgumentNullException(nameof(stopAppAsync));
        if (startWaitBound < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startWaitBound));
        _startWaitBound = startWaitBound;
        _log = log;
    }

    /// <summary>True once a stop has been asked for: no further start will run.</summary>
    public bool StopRequested
    {
        get { lock (_lock) return _stopRequested; }
    }

    /// <summary>
    /// Builds and starts an app, unless a stop has been requested. Returns the started app, or <c>null</c>
    /// when nothing was started because the stop came first (or because the stop gave up waiting for this
    /// very start). Whatever <paramref name="build"/> or <paramref name="start"/> throws propagates to the
    /// caller after the half-started app has been disposed.
    /// </summary>
    public async Task<TApp?> StartAsync(Func<TApp> build, Func<TApp, Task> start)
    {
        lock (_lock)
        {
            if (_stopRequested) return null;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startsInFlight++;
        }

        TApp? app = null;
        try
        {
            app = build();
            var startTask = start(app);
            if (await Task.WhenAny(startTask, _abandoned.Task).ConfigureAwait(false) != startTask)
            {
                // The stop gave up waiting. The start is left to finish (or not) on its own; nobody stops the
                // app, the process is on its way out. Its eventual failure must not go unobserved.
                _ = startTask.ContinueWith(static t => _ = t.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                app = null;
                return null;
            }

            await startTask.ConfigureAwait(false);   // a failed or canceled start throws here

            bool kept;
            lock (_lock)
            {
                // The start won the race against the stop's give-up by a hair: nobody will stop this app.
                kept = !_abandoned.Task.IsCompleted;
                if (kept) _running = app;
            }
            if (!kept)
            {
                try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                app = null;
                return null;
            }

            var started = app;
            app = null;                              // handed over: no dispose in the catch below
            return started;
        }
        catch
        {
            if (app != null)
            {
                try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
        finally
        {
            TaskCompletionSource? idle = null;
            lock (_lock)
            {
                if (--_startsInFlight == 0)
                {
                    idle = _idle;
                    _idle = null;
                }
            }
            idle?.TrySetResult();
        }
    }

    /// <summary>
    /// Requests the stop: from now on no start begins; waits (bounded) for a start in flight; then stops the
    /// running app, if there is one. Safe to call more than once - an app is stopped at most once.
    /// </summary>
    public async Task StopAsync()
    {
        Task? inFlight;
        lock (_lock)
        {
            _stopRequested = true;
            inFlight = _startsInFlight > 0 ? _idle!.Task : null;
        }

        if (inFlight != null)
        {
            try
            {
                await inFlight.WaitAsync(_startWaitBound).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log?.Invoke($"[Node] The front is still starting after {_startWaitBound.TotalSeconds:0.#}s; " +
                             "giving up on it instead of stopping it under its start.");
                _abandoned.TrySetResult();
                return;
            }
        }

        TApp? app;
        lock (_lock)
        {
            app = _running;
            _running = null;
        }
        if (app != null) await _stopAppAsync(app).ConfigureAwait(false);
    }
}

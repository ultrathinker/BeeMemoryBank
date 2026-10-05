namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>
/// The "one task, one cancellation handle" rule of a service that is started again and again ("Back up now" on Android): a start while the
/// task runs is coalesced, BEFORE anything is replaced - it only remembers its start id - and the host's "ended" step runs only when the
/// task's own work has really ended, for the latest start id (so a start that arrives after the task ended is not stopped with it).
/// Before, every start replaced the cancellation source and launched another untracked task; the second one found the job's lock taken, and
/// its end removed the notification and stopped the service while the first backup went on, with no handle to cancel it.
/// </summary>
public sealed class BlindSingleRun
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _lastStartId;

    /// <summary>True while a task runs.</summary>
    public bool IsRunning { get { lock (_gate) return _task is not null; } }

    /// <summary>The start id of the latest start, coalesced or not.</summary>
    public int LastStartId { get { lock (_gate) return _lastStartId; } }

    /// <summary>
    /// Remembers <paramref name="startId"/>; starts <paramref name="work"/> when no task runs (true), otherwise does nothing else (false).
    /// <paramref name="inLock"/> runs first, under the same lock as the end step (the service's StartForeground, which must not interleave
    /// with the end's StopForeground). <paramref name="ended"/> gets the latest start id after the work ended, also when it threw (the
    /// exception is the work's to report; it is not rethrown into the thread pool), under that lock.
    /// </summary>
    public bool TryStart(int startId, Func<CancellationToken, Task> work, Action<int> ended, Action? inLock = null)
    {
        lock (_gate)
        {
            inLock?.Invoke();
            _lastStartId = startId;
            if (_task is not null) return false;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _task = Task.Run(async () =>
            {
                try
                {
                    await work(cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // the work reports its own failures (the log); an escaped one must still end the service
                }
                finally
                {
                    lock (_gate)
                    {
                        _task = null;
                        _cts = null;
                        try { ended(_lastStartId); }
                        finally { cts.Dispose(); }
                    }
                }
            });
            return true;
        }
    }

    /// <summary>Cancels the running task, if any (the service being destroyed).</summary>
    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }
}

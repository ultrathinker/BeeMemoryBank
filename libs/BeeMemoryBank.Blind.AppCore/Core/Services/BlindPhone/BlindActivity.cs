namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>One running operation of the blind copy, registered in <see cref="BlindActivity"/>; dispose it when the operation has really ended.</summary>
public sealed class BlindOperation : IDisposable
{
    private readonly BlindActivity _owner;
    private readonly CancellationTokenSource _linked;
    private int _ended;

    internal BlindOperation(BlindActivity owner, string kind, CancellationTokenSource linked)
    {
        _owner = owner;
        Kind = kind;
        _linked = linked;
    }

    /// <summary>One of the <see cref="BlindActivity"/> kinds.</summary>
    public string Kind { get; }

    /// <summary>Cancelled when the caller's own token is, or when <see cref="BlindActivity.CancelAll"/> is called (the wipe).</summary>
    public CancellationToken Token => _linked.Token;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        _owner.End(this);
        _linked.Dispose();
    }
}

/// <summary>
/// The one list of what the blind copy is doing right now: a sync round, a first load or backup, the "Back up now" service. Every worker
/// registers before it touches the database, the replica files or the keys and unregisters when it has really ended. "Disconnect and
/// wipe" uses it to stop the work and to wait, bounded, until nothing is left running before it touches keys or files, and to refuse
/// new work while it runs (a worker that WorkManager starts a moment too late gets nothing to hold).
/// </summary>
public sealed class BlindActivity
{
    /// <summary>A sync round.</summary>
    public const string Sync = "sync";

    /// <summary>The first load or a backup (<see cref="BlindHeavyWork"/>), from the scheduled worker or the service.</summary>
    public const string Heavy = "heavy";

    /// <summary>The whole task of the "Back up now" foreground service, including its start-up before the job.</summary>
    public const string BackupService = "backup-service";

    private readonly object _gate = new();
    private readonly List<BlindOperation> _active = [];
    private CancellationTokenSource _all = new();
    private TaskCompletionSource _idle = NewIdle(completed: true);
    private bool _closed;

    /// <summary>True while new work is refused (the wipe is running).</summary>
    public bool IsClosed { get { lock (_gate) return _closed; } }

    public int ActiveCount { get { lock (_gate) return _active.Count; } }

    public bool IsActive(string kind) { lock (_gate) return _active.Any(o => o.Kind == kind); }

    /// <summary>The kinds of what is running now, one entry per operation.</summary>
    public IReadOnlyList<string> ActiveKinds() { lock (_gate) return _active.Select(o => o.Kind).ToList(); }

    /// <summary>
    /// Registers an operation. Null when the activity is closed: the caller must not start. <paramref name="ct"/> is the caller's own token;
    /// the returned <see cref="BlindOperation.Token"/> also fires on <see cref="CancelAll"/>.
    /// </summary>
    public BlindOperation? TryBegin(string kind, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_closed) return null;
            var op = new BlindOperation(this, kind, CancellationTokenSource.CreateLinkedTokenSource(_all.Token, ct));
            if (_active.Count == 0) _idle = NewIdle(completed: false);
            _active.Add(op);
            return op;
        }
    }

    internal void End(BlindOperation op)
    {
        lock (_gate)
        {
            _active.Remove(op);
            if (_active.Count == 0) _idle.TrySetResult();
        }
    }

    /// <summary>Refuses new operations until <see cref="Reopen"/>.</summary>
    public void Close()
    {
        lock (_gate) _closed = true;
    }

    /// <summary>Asks every running operation to stop (their tokens fire). It does not wait: see <see cref="WaitIdleAsync"/>.</summary>
    public void CancelAll()
    {
        CancellationTokenSource all;
        lock (_gate) all = _all;
        all.Cancel();
    }

    /// <summary>Accepts operations again (a wipe that could not stop the work in time and gave up).</summary>
    public void Reopen()
    {
        lock (_gate)
        {
            _closed = false;
            if (_all.IsCancellationRequested) _all = new CancellationTokenSource();
        }
    }

    /// <summary>True when no operation is left, false when some is still running after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        if (idle.IsCompleted) return true;
        try
        {
            await idle.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }
}

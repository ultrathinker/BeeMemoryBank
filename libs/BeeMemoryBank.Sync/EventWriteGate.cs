namespace BeeMemoryBank.Sync;

/// <summary>
/// Lets one operation replace the database file under this process with no event write in flight
/// (a blind node's reseed cutover, review L-stage1 #4). Every event write — <see cref="EventApplier.ApplyAsync"/>
/// and a blind node's log trim — runs inside <see cref="EnterAsync"/>; <see cref="QuiesceAsync"/>
/// holds new writes off and waits for the ones already running, so nothing lands in the file being
/// replaced after its last events were read.
///
/// <para>The flow that quiesced still writes through (<see cref="EnterOwnerFlow"/>): the cutover
/// replays events into the new file. A write already inside the gate passes a nested one through
/// too, so a write never waits on itself.</para>
/// </summary>
public sealed class EventWriteGate
{
    public static EventWriteGate Instance { get; } = new();

    private static readonly AsyncLocal<bool> Inside = new();

    private readonly SemaphoreSlim _cutover = new(1, 1);
    private readonly object _lock = new();
    private int _writers;
    private TaskCompletionSource? _drained;

    /// <summary>True while a quiesce is waiting or holds the gate — writes are being held off.</summary>
    public bool IsQuiescing { get; private set; }

    /// <summary>An ordinary event write. Waits while a cutover holds the gate.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        if (Inside.Value) return Passed.Instance;
        await _cutover.WaitAsync(ct);
        lock (_lock) _writers++;
        _cutover.Release();
        return new Writer(this);
    }

    /// <summary>
    /// Marks the calling flow as inside the gate: set by a write that entered (so nested writes pass)
    /// and by the flow that quiesced (so its own replay passes). Synchronous on purpose — the flag
    /// then holds for the caller and everything it awaits.
    /// </summary>
    public static void EnterOwnerFlow() => Inside.Value = true;

    /// <summary>Holds new writes off and returns once none is running; dispose to let them go on.</summary>
    public async Task<IDisposable> QuiesceAsync(CancellationToken ct = default)
    {
        await _cutover.WaitAsync(ct);
        IsQuiescing = true;
        try
        {
            while (true)
            {
                Task drained;
                lock (_lock)
                {
                    if (_writers == 0) break;
                    _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    drained = _drained.Task;
                }
                await drained.WaitAsync(ct);
            }
        }
        catch
        {
            IsQuiescing = false;
            _cutover.Release();
            throw;
        }
        return new Quiesced(this);
    }

    private void ExitWriter()
    {
        lock (_lock)
        {
            if (--_writers == 0 && _drained is { } drained)
            {
                _drained = null;
                drained.TrySetResult();
            }
        }
    }

    private sealed class Writer(EventWriteGate gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.ExitWriter();
        }
    }

    private sealed class Quiesced(EventWriteGate gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            gate.IsQuiescing = false;
            gate._cutover.Release();
        }
    }

    private sealed class Passed : IDisposable
    {
        public static readonly Passed Instance = new();
        public void Dispose() { }
    }
}

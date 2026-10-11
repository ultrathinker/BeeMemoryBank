namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The relay's lock (docs/design/step-b-spec.md 6.6 step 5, 6.8): receipts of a closed node run side by side and hold it shared; the last pass
/// of the import holds it exclusively (a receipt that arrives then waits and lands after the pass, still before the switch to Open or in the
/// normal endpoint after it); a pause for a file copy holds it too, but a receipt that arrives then is not made to wait: it is told to come
/// back (503 <c>vault_sealed_busy</c>), because a copy must not be delayed by a long queue of pushers.
/// </summary>
public sealed class RelayWriteLock
{
    private readonly object _sync = new();
    private int _inFlight;
    private bool _paused;
    private bool _exclusive;
    private TaskCompletionSource _changed = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while a pause for a file copy is asked for or granted.</summary>
    public bool IsPaused
    {
        get { lock (_sync) return _paused; }
    }

    /// <summary>
    /// A receipt's share of the lock, or null when writes are paused (the caller answers busy and stores nothing). Waits while the last
    /// pass of an import holds the lock.
    /// </summary>
    public async Task<IDisposable?> TryEnterReceiptAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_paused) return null;
                if (!_exclusive)
                {
                    _inFlight++;
                    return new Lease(this, LeaseKind.Receipt);
                }
                wait = _changed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The pause of 6.8: from the moment it is asked for no new receipt starts; returns once the ones in flight are done. Disposing the
    /// lease lifts it. A second pause waits for the first.
    /// </summary>
    public async Task<IAsyncDisposable> PauseAsync(CancellationToken ct = default)
    {
        await TakeAsync(paused: true, ct).ConfigureAwait(false);
        return new Lease(this, LeaseKind.Pause);
    }

    /// <summary>The exclusive hold of the import's last pass (6.6 step 5): waits for the receipts in flight; new ones wait for it.</summary>
    public async Task<IDisposable> EnterExclusiveAsync(CancellationToken ct = default)
    {
        await TakeAsync(paused: false, ct).ConfigureAwait(false);
        return new Lease(this, LeaseKind.Exclusive);
    }

    private async Task TakeAsync(bool paused, CancellationToken ct)
    {
        // Claim the lock (waiting while another exclusive holder or pause has it), then wait for the receipts in flight to finish.
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (!_paused && !_exclusive)
                {
                    if (paused) _paused = true; else _exclusive = true;
                    break;
                }
                wait = _changed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_inFlight == 0) return;
                    wait = _changed.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            Release(paused ? LeaseKind.Pause : LeaseKind.Exclusive);
            throw;
        }
    }

    private void Release(LeaseKind kind)
    {
        TaskCompletionSource old;
        lock (_sync)
        {
            switch (kind)
            {
                case LeaseKind.Receipt: _inFlight--; break;
                case LeaseKind.Pause: _paused = false; break;
                default: _exclusive = false; break;
            }
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }

    private enum LeaseKind { Receipt, Pause, Exclusive }

    private sealed class Lease(RelayWriteLock owner, LeaseKind kind) : IDisposable, IAsyncDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(kind);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

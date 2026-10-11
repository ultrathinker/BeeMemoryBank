namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// The process-wide barrier between the operations that author sealed content and the commit of a DEK rotation's new metadata key
/// (docs/design/step-a-spec.md 4.4). An author holds a <b>shared</b> scope for the whole operation, from the check that nothing is
/// pending, through the local change, to the event that describes it; a rotation holds the <b>exclusive</b> scope from before its
/// transaction opens until the new key row and the marker are committed and the new DEK is in the session. So:
/// <list type="bullet">
/// <item>a write cannot be sealed with the old generation and appended after the swap (the key it was sealed with would be one a node
/// the rotation left out can open), because the swap waits for it;</item>
/// <item>a write that starts while the marker stands is refused <i>before</i> it changes anything, and one that starts before the rotation
/// finishes completely, event included: the marker cannot appear in the middle of it.</item>
/// </list>
/// The shared scope is reentrant for one logical flow (a service that calls another service, a service whose event logger enters again),
/// so a rotation waiting for the exclusive scope never deadlocks an author that is already inside. The flow is tracked in an
/// <see cref="AsyncLocal{T}"/>, which is why <see cref="EnterShared"/> is deliberately not an <c>async</c> method: it must set the
/// ambient state in the caller's own execution context.
/// </summary>
public sealed class AuthoringBarrier
{
    /// <summary>How long a rotation waits for the writes in flight before it gives up (nothing has been changed then).</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(120);

    private readonly object _sync = new();
    // Writers (rotations) hold it for their whole scope and readers (authors) pass through it, so a rotation that waits for the
    // authors in flight is not overtaken by new ones: they queue behind it until it is done.
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly AsyncLocal<Flow?> _flow = new();
    private int _inside;
    private TaskCompletionSource? _drained;

    private sealed class Flow
    {
        public int Depth;
    }

    /// <summary>
    /// Enters the shared scope. A flow that is already inside gets a nested scope at once. Not <c>async</c> on purpose (see the type
    /// remarks): the ambient flow is set before the first await, in the caller's context.
    /// </summary>
    public ValueTask<IDisposable> EnterShared(CancellationToken ct = default)
    {
        var flow = _flow.Value;
        if (flow is not null && Volatile.Read(ref flow.Depth) > 0)
        {
            Interlocked.Increment(ref flow.Depth);
            return new ValueTask<IDisposable>(new SharedScope(this, flow));
        }

        flow = new Flow { Depth = 1 };
        _flow.Value = flow;
        return EnterOuterAsync(flow, ct);
    }

    private async ValueTask<IDisposable> EnterOuterAsync(Flow flow, CancellationToken ct)
    {
        try
        {
            await _turnstile.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_sync) _inside++;
            }
            finally
            {
                _turnstile.Release();
            }
        }
        catch
        {
            Volatile.Write(ref flow.Depth, 0); // the caller's ambient flow must not count as inside
            throw;
        }
        return new SharedScope(this, flow);
    }

    /// <summary>
    /// Enters the exclusive scope: waits for every shared scope to end and keeps new ones out until the returned scope is disposed.
    /// The holder must not enter a shared scope itself. Throws <see cref="TimeoutException"/> when the writes in flight do not end
    /// within <paramref name="drainTimeout"/> (default <see cref="DefaultDrainTimeout"/>); the barrier is then free again.
    /// </summary>
    public async Task<IDisposable> EnterExclusiveAsync(TimeSpan? drainTimeout = null, CancellationToken ct = default)
    {
        await _turnstile.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task drained;
            lock (_sync)
            {
                if (_inside == 0)
                {
                    drained = Task.CompletedTask;
                }
                else
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    drained = _drained.Task;
                }
            }
            if (!drained.IsCompleted)
                await drained.WaitAsync(drainTimeout ?? DefaultDrainTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
        finally
        {
            lock (_sync) _drained = null;
        }
        return new ExclusiveScope(_turnstile);
    }

    private void LeaveOuter()
    {
        lock (_sync)
        {
            if (--_inside == 0) _drained?.TrySetResult();
        }
    }

    private sealed class SharedScope(AuthoringBarrier owner, Flow flow) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref flow.Depth) != 0) return;
            if (ReferenceEquals(owner._flow.Value, flow)) owner._flow.Value = null;
            owner.LeaveOuter();
        }
    }

    private sealed class ExclusiveScope(SemaphoreSlim turnstile) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) turnstile.Release();
        }
    }
}

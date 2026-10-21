namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>Who holds the device's gate: the account (its id and its name as the person calls it), since when, and when the scheduler gave up waiting for it to stop.</summary>
internal sealed record BlindGateHolder(string? OwnerId, string? OwnerName, DateTimeOffset Since, DateTimeOffset? AbandonedSince)
{
    /// <summary>True when the holder's job was asked to stop, did not, and was let go of: it still holds the gate, and nothing can end it from outside.</summary>
    public bool Abandoned => AbandonedSince is not null;
}

/// <summary>
/// The one lock for a long job (a first load, a backup) on a DEVICE: with several accounts, two long jobs at once would double the disk and the traffic that the limits are written for, so
/// they take turns. Cooperating callers hold it only for the length of their job and give it back (<see cref="TryEnter()"/> returns a lease; dispose it when the job has really ended).
/// A per-account lock does not do this (it protects one account from itself, not the others from it) and the HTTP layer cannot, so the host builds ONE gate and passes it to every
/// runtime (<c>BlindAppOptions.DeviceGate</c>); a composition that is given none makes its own, which for a copy with a single account is the same as the lock it always had.
///
/// <para><b>It records who holds it.</b> A thread cannot be killed: a job that ignores its cancellation holds the gate until it returns, and the scheduler can only stop waiting for it
/// (<see cref="Abandon"/>). The gate then says so: the holder, since when, and that it was let go of, so that another account's long job is told "the device is busy: that account's job did
/// not stop" instead of "Already running", the card of the account says so, and nobody retries in a hurry for a gate that cannot be freed from outside.</para>
/// </summary>
public sealed class BlindDeviceGate
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _info = new();
    private Holder? _holder;
    private TimeProvider _time = TimeProvider.System;

    /// <summary>True while a long job holds the gate.</summary>
    public bool IsBusy => _one.CurrentCount == 0;

    /// <summary>The clock the "since when" is read from (the host's).</summary>
    internal void UseClock(TimeProvider time)
    {
        lock (_info) _time = time;
    }

    /// <summary>
    /// Takes the gate if it is free, otherwise returns null at once (the caller says "Already running." and the scheduler asks again later).
    /// Dispose the lease when the job has really ended.
    /// </summary>
    public IDisposable? TryEnter() => TryEnter(null, null);

    /// <summary>The same, recording the account that holds the gate: <paramref name="ownerId"/> and the name its card shows (read when it is asked for).</summary>
    internal IDisposable? TryEnter(string? ownerId, Func<string>? ownerName)
    {
        if (!_one.Wait(0)) return null;
        var holder = new Holder(ownerId, ownerName);
        lock (_info)
        {
            holder.Since = _time.GetUtcNow();
            _holder = holder;
        }
        return new Lease(this, holder);
    }

    /// <summary>Who holds the gate now, or null.</summary>
    internal BlindGateHolder? CurrentHolder
    {
        get
        {
            Holder? holder;
            DateTimeOffset since;
            DateTimeOffset? abandoned;
            lock (_info)
            {
                holder = _holder;
                if (holder is null) return null;
                since = holder.Since;
                abandoned = holder.AbandonedSince;
            }
            string? name = null;
            try { name = holder.OwnerName?.Invoke(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            return new BlindGateHolder(holder.OwnerId, name, since, abandoned);
        }
    }

    /// <summary>The scheduler gave up waiting for the job of <paramref name="ownerId"/> that holds the gate: it stays recorded as let go of until the job really ends. False when that account does not hold the gate.</summary>
    internal bool Abandon(string ownerId)
    {
        lock (_info)
        {
            if (_holder is not { } holder || holder.OwnerId != ownerId) return false;
            holder.AbandonedSince ??= _time.GetUtcNow();
            return true;
        }
    }

    private sealed class Holder(string? ownerId, Func<string>? ownerName)
    {
        public string? OwnerId { get; } = ownerId;
        public Func<string>? OwnerName { get; } = ownerName;
        public DateTimeOffset Since { get; set; }
        public DateTimeOffset? AbandonedSince { get; set; }
    }

    private sealed class Lease(BlindDeviceGate gate, Holder holder) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (gate._info)
            {
                if (ReferenceEquals(gate._holder, holder)) gate._holder = null;
            }
            gate._one.Release();
        }
    }
}

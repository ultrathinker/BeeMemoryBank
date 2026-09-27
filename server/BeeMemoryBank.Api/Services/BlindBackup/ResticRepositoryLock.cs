namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// One restic process at a time against the node's repositories. Jobs already run one at a
/// time, but the console's snapshot list is not a job: without this it would start a second
/// restic in the middle of a backup — racing restic's own locks and sharing Pause and cancel with
/// the backup's process. Jobs wait for the lock; the list does not wait (see
/// <see cref="TryAcquire"/>) because a request cannot sit out a multi-hour backup.
/// </summary>
public sealed class ResticRepositoryLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Release(_gate);
    }

    /// <summary>The lock if it is free right now, else null.</summary>
    public IDisposable? TryAcquire() => _gate.Wait(0) ? new Release(_gate) : null;

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
        }
    }
}

/// <summary>The repository is in use by a job; the caller should ask again once it finishes.</summary>
public sealed class ResticRepositoryBusyException()
    : InvalidOperationException("a backup job is using the repository; try again when it finishes");

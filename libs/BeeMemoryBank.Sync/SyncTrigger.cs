using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync;

public class SyncTrigger : ISyncTrigger
{
    private volatile int _signalFlag;
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        if (Interlocked.Exchange(ref _signalFlag, 1) == 0)
            _semaphore.Release();
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var triggered = await _semaphore.WaitAsync(timeout, ct);
        Interlocked.Exchange(ref _signalFlag, 0);
        return triggered;
    }
}

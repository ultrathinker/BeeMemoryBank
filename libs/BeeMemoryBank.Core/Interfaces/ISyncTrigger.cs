namespace BeeMemoryBank.Core.Interfaces;

public interface ISyncTrigger
{
    void Signal();

    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}

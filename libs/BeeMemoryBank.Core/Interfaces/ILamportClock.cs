namespace BeeMemoryBank.Core.Interfaces;

public interface ILamportClock
{
    /// <summary>Increments the counter and returns the new value.</summary>
    long Tick();

    /// <summary>Updates the counter: sets max(local, remote) + 1.</summary>
    void Update(long remoteTs);

    long Current { get; }
}

using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Thread-safe Lamport counter.
/// Singleton; calling Initialize() at startup restores the value from the database.
/// </summary>
public sealed class LamportClock : ILamportClock
{
    private long _current;
    private int _initialized; // 0 = no, 1 = yes

    /// <summary>
    /// Restores the clock from the database.
    /// Must be called once at startup, before processing any requests.
    /// </summary>
    public void Initialize(long maxKnownTs)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
            Interlocked.Exchange(ref _current, maxKnownTs);
    }

    /// <summary>Increments the counter and returns the new value.</summary>
    public long Tick() => Interlocked.Increment(ref _current);

    /// <summary>Sets max(local, remote) + 1, thread-safe.</summary>
    public void Update(long remoteTs)
    {
        long current;
        long desired;
        do
        {
            current = Interlocked.Read(ref _current);
            desired = Math.Max(current, remoteTs) + 1;
            if (desired <= current) return; // already greater
        }
        while (Interlocked.CompareExchange(ref _current, desired, current) != current);
    }

    public long Current => Interlocked.Read(ref _current);
}

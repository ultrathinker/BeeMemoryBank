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
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

        // Use max(current, maxKnownTs): if Tick()/Update() have already run before
        // Initialize completes (e.g., a sync pull happens during startup before the DB
        // read finishes), Interlocked.Exchange would clobber a higher counter and
        // produce non-monotonic timestamps. (kilo-sync R2 finding F5.)
        long current;
        do
        {
            current = Interlocked.Read(ref _current);
            if (current >= maxKnownTs) return;
        }
        while (Interlocked.CompareExchange(ref _current, maxKnownTs, current) != current);
    }

    /// <summary>Increments the counter and returns the new value.</summary>
    public long Tick() => Interlocked.Increment(ref _current);

    /// <summary>
    /// Sets max(local, remote) + 1, thread-safe. Clamps remote to prevent overflow:
    /// a malicious or buggy peer sending long.MaxValue would otherwise wrap to
    /// long.MinValue (unchecked Math.Max + 1) and corrupt the local clock forever.
    /// Cap = current + MaxJump (10 million ticks, ~115 days at 1 tick/sec).
    /// (Wave 2 audit claude-C #3 / gemini #4.)
    /// </summary>
    public void Update(long remoteTs)
    {
        const long MaxJump = 10_000_000L;
        long current;
        long desired;
        do
        {
            current = Interlocked.Read(ref _current);
            // Clamp clearly-unreasonable values (e.g. long.MaxValue) so they can't
            // overflow when we add 1. Legitimate large jumps (e.g. peer that's been
            // active for years) still work — MaxJump is generous.
            var cap = current > long.MaxValue - MaxJump ? long.MaxValue : current + MaxJump;
            var clampedRemote = remoteTs > cap ? cap : remoteTs;
            desired = Math.Max(current, clampedRemote) + 1;
            if (desired <= current) return; // already greater
        }
        while (Interlocked.CompareExchange(ref _current, desired, current) != current);
    }

    public long Current => Interlocked.Read(ref _current);
}

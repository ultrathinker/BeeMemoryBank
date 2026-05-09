using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Used in Phase 1 and tests without sync.
/// Increments locally, not restored from the database.
/// </summary>
public sealed class NullLamportClock : ILamportClock
{
    private long _current;

    public long Tick() => Interlocked.Increment(ref _current);

    public void Update(long remoteTs) { } // no-op for Phase 1

    public long Current => Interlocked.Read(ref _current);
}

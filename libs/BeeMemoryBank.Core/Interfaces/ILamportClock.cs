namespace BeeMemoryBank.Core.Interfaces;

public interface ILamportClock
{
    /// <summary>Increments the counter and returns the new value.</summary>
    long Tick();

    /// <summary>Updates the counter: sets max(local, remote) + 1.</summary>
    void Update(long remoteTs);

    /// <summary>
    /// Moves the counter to at least <paramref name="value"/>, without <see cref="Update"/>'s per-step cap. For a node that has just taken over rows
    /// stamped higher than any step <see cref="Update"/> would allow (a join from a snapshot, a restore); the caller bounds <paramref name="value"/>.
    /// </summary>
    void RaiseTo(long value);

    long Current { get; }
}

using System.Collections.Concurrent;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Peers the scheduler could not reach, and when to try each again (plan 4.4): the pause doubles
/// with every consecutive failure, from one sync interval up to <see cref="MaxPause"/>. A phone out
/// of the house, or a blind node on a switched-off box, then costs one attempt now and then instead
/// of a timeout every cycle; a PC coming back to its hub waits at most <see cref="MaxPause"/>.
/// </summary>
public sealed class UnreachablePeers
{
    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, (int Failures, DateTime NextAttemptUtc)> _peers = new();

    public bool ShouldSkip(Guid nodeId, DateTime nowUtc) =>
        _peers.TryGetValue(nodeId, out var state) && nowUtc < state.NextAttemptUtc;

    /// <summary>Records a failed attempt; returns how many in a row and the pause before the next.</summary>
    public (int Failures, TimeSpan Pause) NoteFailure(Guid nodeId, DateTime nowUtc, TimeSpan interval)
    {
        var failures = _peers.TryGetValue(nodeId, out var previous) ? previous.Failures + 1 : 1;
        var pause = TimeSpan.FromTicks(Math.Min(MaxPause.Ticks, interval.Ticks * (1L << Math.Min(failures - 1, 16))));
        _peers[nodeId] = (failures, nowUtc + pause);
        return (failures, pause);
    }

    /// <summary>Forgets the streak; returns how many attempts had failed before (0 if none).</summary>
    public int NoteSuccess(Guid nodeId) => _peers.TryRemove(nodeId, out var was) ? was.Failures : 0;
}

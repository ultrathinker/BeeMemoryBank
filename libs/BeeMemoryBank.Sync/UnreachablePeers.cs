using System.Collections.Concurrent;
using System.Net.NetworkInformation;

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

    /// <summary>
    /// A gap this long between two failed attempts means this computer was asleep or off in between: awake, the next attempt
    /// comes at most <see cref="MaxPause"/> plus one cycle later. The streak's start (<see cref="FailingSince"/>) then moves to the
    /// new failure, so the hours a PC slept through are not counted as hours the peer was down (BMB-77).
    /// </summary>
    public static readonly TimeSpan StreakBreak = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (int Failures, DateTime NextAttemptUtc, DateTime SinceUtc, DateTime LastUtc)> _peers = new();
    private readonly Func<bool> _networkUp;

    /// <param name="networkUp">Whether this computer has a network at all (default: the operating system's answer). While it has none, a failed call says
    /// nothing about the peer: the back-off still grows, but the streak does not (a laptop on a train would otherwise report every server it calls as down).</param>
    public UnreachablePeers(Func<bool>? networkUp = null) => _networkUp = networkUp ?? SystemNetworkIsUp;

    // An answer that cannot be had counts as "up": no streak is ever hidden because the check itself failed.
    private static bool SystemNetworkIsUp()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch (Exception) { return true; }
    }

    public bool ShouldSkip(Guid nodeId, DateTime nowUtc) =>
        _peers.TryGetValue(nodeId, out var state) && nowUtc < state.NextAttemptUtc;

    /// <summary>Records a failed attempt; returns how many in a row and the pause before the next.</summary>
    public (int Failures, TimeSpan Pause) NoteFailure(Guid nodeId, DateTime nowUtc, TimeSpan interval)
    {
        var known = _peers.TryGetValue(nodeId, out var previous);
        var failures = known ? previous.Failures + 1 : 1;
        var pause = TimeSpan.FromTicks(Math.Min(MaxPause.Ticks, interval.Ticks * (1L << Math.Min(failures - 1, 16))));
        var since = known && nowUtc - previous.LastUtc < StreakBreak && _networkUp() ? previous.SinceUtc : nowUtc;
        _peers[nodeId] = (failures, nowUtc + pause, since, nowUtc);
        return (failures, pause);
    }

    /// <summary>
    /// Each peer that is failing now, with the time its streak of failures began while this computer was awake
    /// (see <see cref="StreakBreak"/>). A snapshot; a peer that answered again is not in it.
    /// </summary>
    public IReadOnlyDictionary<Guid, DateTime> FailingSince() => _peers.ToDictionary(p => p.Key, p => p.Value.SinceUtc);

    /// <summary>Forgets the streak; returns how many attempts had failed before (0 if none).</summary>
    public int NoteSuccess(Guid nodeId) => _peers.TryRemove(nodeId, out var was) ? was.Failures : 0;

    /// <summary>Forgets a peer whose address changed or that is gone: the pause and the streak were earned at another address.</summary>
    public void Forget(Guid nodeId) => _peers.TryRemove(nodeId, out _);
}

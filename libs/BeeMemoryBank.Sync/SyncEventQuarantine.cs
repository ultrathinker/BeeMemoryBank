using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Tracks events that repeatedly fail to apply during pull, and quarantines one once it has
/// failed too many times in a row (M5c).
///
/// <para>
/// Before this existed, <see cref="SyncClient"/>'s pull loop stopped at the FIRST event that threw
/// (bad signature, whitelist ordering, any other permanent failure) and left the sync cursor
/// exactly where it was: every subsequent cycle re-fetched the same page, hit the same event
/// first, and stopped again — forever. Two problems, not one: the node made zero forward progress
/// even on other, perfectly fine events later in that page, and nothing beyond a repeating log
/// line at WARNING/ERROR ever told an operator this was happening.
/// </para>
///
/// <para>
/// A TRANSIENT failure (a network blip mid-batch, a momentarily-locked local DB) should still stop
/// the loop and retry from the same position next cycle — that's the existing, correct behavior
/// for something that might just work next time. Only once the SAME event has failed
/// <see cref="QuarantineThreshold"/> times in a row does it get skipped: the cursor advances past
/// it and the rest of the page keeps being applied. This is a judgment call the codebase already
/// makes with the same shape for the replay shield and hard-delete gate (skip-and-move-on beats
/// blocking sync indefinitely on one bad event) but with an explicit, visible marker instead of a
/// silent drop.
/// </para>
///
/// <para>
/// M5 follow-up: this USED to be a static, purely in-memory <c>ConcurrentDictionary</c> (see git
/// history) for the same reason <see cref="BeeMemoryBank.Core.Services.ArticleWriteLock"/> is
/// static — a lightweight tracker needing neither a migration nor DI registration. That tradeoff
/// turned out to be wrong in practice: a node restart forgot every recorded failure, so a
/// permanently-bad event that had just been quarantined started blocking the pull loop again on
/// the very next cycle after the restart — and a stuck sync is exactly the situation most likely
/// to make an operator reach for a restart. It is now backed by
/// <see cref="ISyncQuarantineRepository"/> (durable, survives restart) instead of the dictionary.
/// The class stays a static, stateless helper taking its dependency as a parameter — the same
/// shape <see cref="PeerAuthenticator"/> already uses for exactly this reason (see its own remarks)
/// — rather than becoming a DI-registered instance service, since every caller already has (or can
/// trivially obtain via DI) an <see cref="ISyncQuarantineRepository"/> to pass in, and the
/// threshold-comparison logic here doesn't need any instance state of its own.
/// </para>
///
/// <para>
/// Keyed by the event's own EventId (globally unique) rather than (peer, EventId): the same
/// problem event can be pulled from more than one peer in a mesh via gossip relay, and all of
/// those pulls should count toward, and see, the same quarantine record.
/// </para>
/// </summary>
public static class SyncEventQuarantine
{
    /// <summary>Consecutive failures before an event is treated as permanently skipped.</summary>
    public const int QuarantineThreshold = 5;

    public sealed record Entry(
        Guid EventId,
        string EventType,
        Guid OriginNodeId,
        int FailureCount,
        DateTime FirstFailedAtUtc,
        DateTime LastFailedAtUtc,
        string LastError,
        bool Quarantined);

    /// <summary>
    /// Records one failed apply attempt for <paramref name="eventId"/>. Returns true once this
    /// event has now failed <see cref="QuarantineThreshold"/> times in a row and should be skipped
    /// rather than retried; false if the caller should keep the existing "stop and retry next
    /// cycle" behavior.
    /// </summary>
    public static async Task<bool> RecordFailureAsync(
        ISyncQuarantineRepository repo, Guid eventId, string eventType, Guid originNodeId, string error)
    {
        var entry = await repo.RecordFailureAsync(eventId, eventType, originNodeId, error);
        return entry.FailureCount >= QuarantineThreshold;
    }

    /// <summary>
    /// Clears any tracked failures for an event — automatically, once it applies successfully, or
    /// via an operator-triggered "clear / retry" action (<c>DELETE /api/sync/quarantine/{eventId}</c>
    /// in SyncEndpoints.cs) once the underlying cause has been fixed. Resets the failure streak to
    /// zero; it does NOT by itself force the event to be re-delivered — see the endpoint's own
    /// comment for that caveat.
    /// </summary>
    public static Task ClearFailureAsync(ISyncQuarantineRepository repo, Guid eventId) => repo.ClearAsync(eventId);

    /// <summary>
    /// Every event with at least one recorded failure, most-failed first — surfaced via
    /// <c>GET /api/sync/quarantine</c> so an operator has somewhere to look beyond log lines.
    /// </summary>
    public static async Task<List<Entry>> ListAllAsync(ISyncQuarantineRepository repo)
    {
        var rows = await repo.GetAllAsync();
        return rows
            .Select(e => new Entry(
                e.EventId, e.EventType, e.OriginNodeId, e.FailureCount,
                e.FirstFailedAtUtc, e.LastFailedAtUtc, e.LastError,
                e.FailureCount >= QuarantineThreshold))
            .OrderByDescending(e => e.FailureCount)
            .ToList();
    }
}

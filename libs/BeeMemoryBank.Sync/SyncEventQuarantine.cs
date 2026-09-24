using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Tracks events that repeatedly fail to apply during pull, and quarantines one once it has
/// failed too many times in a row.
///
/// <para>
/// Without it, <see cref="SyncClient"/>'s pull loop would stop at the FIRST event that throws
/// (bad signature, whitelist ordering, any other permanent failure) with the sync cursor
/// unchanged, and every subsequent cycle would hit the same event and stop again — forever. The
/// node would make zero forward progress even on perfectly fine events later in that page, and
/// nothing beyond a repeating log line would tell an operator.
/// </para>
///
/// <para>
/// A TRANSIENT failure (a network blip mid-batch, a momentarily-locked local DB) should still stop
/// the loop and retry from the same position next cycle — the correct behavior for something that
/// might just work next time. Only once the SAME event has failed
/// <see cref="QuarantineThreshold"/> times in a row does it get skipped: the cursor advances past
/// it and the rest of the page keeps being applied. This is a judgment call the codebase already
/// makes with the same shape for the replay shield and hard-delete gate (skip-and-move-on beats
/// blocking sync indefinitely on one bad event) but with an explicit, visible marker instead of a
/// silent drop.
/// </para>
///
/// <para>
/// Backed by <see cref="ISyncQuarantineRepository"/> (durable), not an in-memory map: a restart
/// must not forget recorded failures, or a just-quarantined event blocks the pull loop again on
/// the very next cycle — and a stuck sync is exactly the situation most likely to make an
/// operator reach for a restart. The class stays a static, stateless helper taking its dependency as a parameter — the same
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
///
/// <para>
/// Not every failure is permanent. A whitelist_add for the originating node
/// that has not arrived yet, a blob the transport has not delivered yet, and a DEK rotation COMMIT
/// that outran its own PROPOSED all resolve themselves given enough time — usually minutes,
/// sometimes hours, never the five-cycle (roughly five-minute) budget <see cref="QuarantineThreshold"/>
/// gives a genuinely broken event. <see cref="SyncFailureClassifier"/> sorts a failure's exception
/// into <see cref="SyncFailureKind.Permanent"/> or <see cref="SyncFailureKind.Deferred"/>, and each
/// kind is tracked and budgeted separately on the SAME row (<see cref="SyncQuarantineEntry"/>) —
/// see its own remarks for why deferred failures must never count toward the permanent budget.
/// </para>
/// </summary>
public static class SyncEventQuarantine
{
    /// <summary>Consecutive PERMANENT failures before an event is treated as permanently skipped.</summary>
    public const int QuarantineThreshold = 5;

    /// <summary>
    /// How long a DEFERRED failure is retried before it, too, is given up on. Measured as wall-clock
    /// time since the event's FIRST recorded failure (of either kind) rather than as an attempt
    /// count: <see cref="SyncScheduler"/>'s push-on-save trigger means a busy node can retry the
    /// same event many times inside one minute, so an attempt-count budget meant to span hours at
    /// the default 60-second interval could be exhausted in minutes under load — breaking the
    /// "hours, not minutes" guarantee this budget exists to give.
    /// </summary>
    public static readonly TimeSpan DeferredQuarantineBudget = TimeSpan.FromHours(6);

    public sealed record Entry(
        Guid EventId,
        string EventType,
        Guid OriginNodeId,
        int PermanentFailureCount,
        int DeferredFailureCount,
        DateTime FirstFailedAtUtc,
        DateTime LastFailedAtUtc,
        string LastError,
        SyncFailureKind LastFailureKind,
        bool Quarantined)
    {
        public int FailureCount => PermanentFailureCount + DeferredFailureCount;
    }

    /// <summary>
    /// Records one failed apply attempt for <paramref name="eventId"/>, classifying
    /// <paramref name="ex"/> via <see cref="SyncFailureClassifier"/> to decide which of the two
    /// budgets it draws from. Returns true once this event should be treated as skipped rather
    /// than retried — see <see cref="IsQuarantined"/> for the exact rule; false if the caller
    /// should keep the existing "stop and retry next cycle" behavior.
    /// </summary>
    public static Task<bool> RecordFailureAsync(
        ISyncQuarantineRepository repo, Guid eventId, string eventType, Guid originNodeId, Exception ex)
        => RecordFailureAsync(repo, eventId, eventType, originNodeId, ex.Message, SyncFailureClassifier.Classify(ex));

    /// <summary>
    /// Same as the <see cref="Exception"/> overload, for callers with no exception object to
    /// classify from — e.g. SyncClient's push-too-large-even-alone quarantine, which is not
    /// modeled as a thrown exception at all. Always <see cref="SyncFailureKind.Permanent"/>: the
    /// safe default when nothing has classified the failure as deferrable.
    /// </summary>
    public static Task<bool> RecordFailureAsync(
        ISyncQuarantineRepository repo, Guid eventId, string eventType, Guid originNodeId, string error)
        => RecordFailureAsync(repo, eventId, eventType, originNodeId, error, SyncFailureKind.Permanent);

    private static async Task<bool> RecordFailureAsync(
        ISyncQuarantineRepository repo, Guid eventId, string eventType, Guid originNodeId, string error, SyncFailureKind kind)
    {
        var entry = await repo.RecordFailureAsync(eventId, eventType, originNodeId, error, kind);
        return IsQuarantined(entry, DateTime.UtcNow);
    }

    /// <summary>
    /// Pure decision, no I/O — same shape as <see cref="ConflictResolver.IncomingWins"/> and for
    /// the same reason: one place callers ask "is this event done being retried", rather than each
    /// re-deriving the rule. A permanent failure is quarantined once it alone has reached
    /// <see cref="QuarantineThreshold"/>; one with any deferred failure in its history is also
    /// judged by whether <see cref="DeferredQuarantineBudget"/> has elapsed
    /// since its first recorded failure of either kind — see that constant's own remarks for why
    /// time, not attempt count.
    /// </summary>
    public static bool IsQuarantined(SyncQuarantineEntry entry, DateTime nowUtc) =>
        entry.PermanentFailureCount >= QuarantineThreshold
        // Any deferral in this event's history puts it under the time budget — not just a deferral
        // on its MOST RECENT attempt. Gating on the last kind alone would let an event with a few
        // permanent failures (below the threshold) and a permanent latest attempt escape the time
        // check no matter how old it is, sitting un-quarantined indefinitely and under-reporting
        // its staleness in the operator's quarantine view.
        || (entry.DeferredFailureCount > 0
            && nowUtc - entry.FirstFailedAtUtc >= DeferredQuarantineBudget);

    /// <summary>
    /// Clears any tracked failures for an event — automatically, once it applies successfully, or
    /// via an operator-triggered "clear / retry" action (<c>DELETE /api/sync/quarantine/{eventId}</c>
    /// in SyncEndpoints.cs) once the underlying cause has been fixed. Resets both failure counters
    /// to zero; it does NOT by itself force the event to be re-delivered — see the endpoint's own
    /// comment for that caveat.
    /// </summary>
    public static Task ClearFailureAsync(ISyncQuarantineRepository repo, Guid eventId) => repo.ClearAsync(eventId);

    /// <summary>
    /// Every event with at least one recorded failure, most-failed first — surfaced via
    /// <c>GET /api/sync/quarantine</c> so an operator has somewhere to look beyond log lines. Both
    /// LastFailureKind and the permanent/deferred split are included precisely so an operator can
    /// tell "quarantined, genuinely broken" apart from "still retrying, waiting on a precondition"
    /// instead of seeing one undifferentiated failure count.
    /// </summary>
    public static async Task<List<Entry>> ListAllAsync(ISyncQuarantineRepository repo)
    {
        var rows = await repo.GetAllAsync();
        var now = DateTime.UtcNow;
        return rows
            .Select(e => new Entry(
                e.EventId, e.EventType, e.OriginNodeId, e.PermanentFailureCount, e.DeferredFailureCount,
                e.FirstFailedAtUtc, e.LastFailedAtUtc, e.LastError, e.LastFailureKind,
                IsQuarantined(e, now)))
            .OrderByDescending(e => e.FailureCount)
            .ToList();
    }
}

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Whether a failed sync-event apply attempt is worth retrying for a long time or should be
/// quarantined promptly. See <c>BeeMemoryBank.Sync.SyncFailureClassifier</c> for the single place
/// an exception is turned into one of these — this enum only needs to live here, rather than in
/// BeeMemoryBank.Sync alongside its classifier, because <see cref="SyncQuarantineEntry"/> (a
/// Core/repository-layer type) has to store it and Core cannot depend on Sync.
/// </summary>
public enum SyncFailureKind
{
    /// <summary>Nothing about waiting changes the answer: bad signature, malformed payload,
    /// unknown/unresolvable data. Quarantined once alone it reaches
    /// <c>SyncEventQuarantine.QuarantineThreshold</c> consecutive failures.</summary>
    Permanent,

    /// <summary>A precondition this node does not yet hold: the originator is not (yet)
    /// whitelisted, a referenced blob has not arrived, a DEK rotation's PROPOSED predecessor is
    /// still missing. Retried for far longer — see
    /// <c>SyncEventQuarantine.DeferredQuarantineBudget</c> — before it, too, is given up on.</summary>
    Deferred
}

/// <summary>
/// One event's cross-restart failure-tracking record for the sync pull/push quarantine (M5c/M5
/// follow-up). See <c>BeeMemoryBank.Sync.SyncEventQuarantine</c> for the "why quarantine exists at
/// all" explanation and the threshold comparisons this backs.
///
/// <para>
/// PermanentFailureCount and DeferredFailureCount are tracked SEPARATELY, not as one combined
/// counter, because a deferred failure (whitelist/blob/rotation precondition missing) must never
/// count toward the short permanent-failure budget — an event stuck waiting on a slow-to-arrive
/// whitelist_add would otherwise get quarantined at 5 attempts exactly like a bad signature would,
/// which is the bug this record shape exists to prevent. The same EventId can flip classification
/// across attempts (e.g. blob-missing today, a genuinely corrupt payload once the blob arrives
/// tomorrow) — each attempt's failure is credited to whichever counter its OWN exception
/// classifies as, never both.
/// </para>
///
/// Neither counter is stored as a derived "IsQuarantined" flag — the threshold constants live in
/// code, not in this row, so raising either one later needs no migration or backfill.
/// </summary>
public record SyncQuarantineEntry(
    Guid EventId,
    string EventType,
    Guid OriginNodeId,
    int PermanentFailureCount,
    int DeferredFailureCount,
    DateTime FirstFailedAtUtc,
    DateTime LastFailedAtUtc,
    string LastError,
    SyncFailureKind LastFailureKind)
{
    /// <summary>Total attempts recorded, of either kind. Kept for callers that only want "how many
    /// times has this failed overall" (e.g. the admin UI's default sort) without caring which
    /// budget each attempt drew from.</summary>
    public int FailureCount => PermanentFailureCount + DeferredFailureCount;
}

public interface ISyncQuarantineRepository
{
    /// <summary>
    /// Records one failed apply/push attempt for <paramref name="eventId"/>: inserts a new row, or
    /// atomically increments the counter matching <paramref name="kind"/> on an existing row and
    /// refreshes LastFailedAtUtc/LastError/LastFailureKind, while leaving FirstFailedAtUtc
    /// untouched. Returns the row AFTER the update so the caller can compare its counters against
    /// the relevant quarantine threshold without a second round-trip.
    /// </summary>
    Task<SyncQuarantineEntry> RecordFailureAsync(Guid eventId, string eventType, Guid originNodeId, string error, SyncFailureKind kind);

    /// <summary>
    /// Removes the tracking row for <paramref name="eventId"/> — called both automatically (the
    /// event applied/pushed cleanly on a later attempt) and from an operator-triggered "clear /
    /// retry" action once the underlying cause has been fixed. A no-op if no row exists.
    /// </summary>
    Task ClearAsync(Guid eventId);

    /// <summary>Every event with at least one recorded failure, most-failed first.</summary>
    Task<List<SyncQuarantineEntry>> GetAllAsync();
}

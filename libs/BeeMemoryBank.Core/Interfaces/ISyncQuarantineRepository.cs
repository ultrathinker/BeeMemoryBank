namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// One event's cross-restart failure-tracking record for the sync pull/push quarantine (M5c/M5
/// follow-up). See <c>BeeMemoryBank.Sync.SyncEventQuarantine</c> for the "why quarantine exists at
/// all" explanation and the FailureCount/threshold comparison this backs.
///
/// FailureCount is stored raw, not as a derived "IsQuarantined" flag — the threshold constant
/// (SyncEventQuarantine.QuarantineThreshold) lives in code, not in this row, so raising it later
/// needs no migration or backfill of already-persisted rows.
/// </summary>
public record SyncQuarantineEntry(
    Guid EventId,
    string EventType,
    Guid OriginNodeId,
    int FailureCount,
    DateTime FirstFailedAtUtc,
    DateTime LastFailedAtUtc,
    string LastError);

public interface ISyncQuarantineRepository
{
    /// <summary>
    /// Records one failed apply/push attempt for <paramref name="eventId"/>: inserts a new row at
    /// FailureCount 1, or atomically increments an existing row's FailureCount and refreshes
    /// LastFailedAtUtc/LastError while leaving FirstFailedAtUtc untouched. Returns the row AFTER
    /// the update so the caller can compare FailureCount against the quarantine threshold without
    /// a second round-trip.
    /// </summary>
    Task<SyncQuarantineEntry> RecordFailureAsync(Guid eventId, string eventType, Guid originNodeId, string error);

    /// <summary>
    /// Removes the tracking row for <paramref name="eventId"/> — called both automatically (the
    /// event applied/pushed cleanly on a later attempt) and from an operator-triggered "clear /
    /// retry" action once the underlying cause has been fixed. A no-op if no row exists.
    /// </summary>
    Task ClearAsync(Guid eventId);

    /// <summary>Every event with at least one recorded failure, most-failed first.</summary>
    Task<List<SyncQuarantineEntry>> GetAllAsync();
}

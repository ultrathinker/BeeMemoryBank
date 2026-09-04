using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class SyncQuarantineRepository(DbConnectionFactory factory) : BaseRepository(factory), ISyncQuarantineRepository
{
    private const string SelectColumns = @"
        event_id                AS EventId,
        event_type               AS EventType,
        origin_node_id           AS OriginNodeId,
        failure_count            AS PermanentFailureCount,
        deferred_failure_count   AS DeferredFailureCount,
        first_failed_at_utc      AS FirstFailedAtUtc,
        last_failed_at_utc       AS LastFailedAtUtc,
        last_error               AS LastError,
        last_failure_kind        AS LastFailureKind";

    public async Task<SyncQuarantineEntry> RecordFailureAsync(Guid eventId, string eventType, Guid originNodeId, string error, SyncFailureKind kind)
    {
        using var conn = OpenConnection();
        var now = DateTime.UtcNow;
        var kindText = ToKindText(kind);

        // Single atomic INSERT..ON CONFLICT instead of read-then-increment-then-write: the same
        // globally-unique EventId can be recorded as failed from more than one peer relay in short
        // succession (see the table's own comment on origin_node_id), and SQLite's single-writer
        // model makes this statement atomic without needing an app-level lock. FirstFailedAtUtc is
        // deliberately left OUT of the UPDATE clause — only the INSERT branch sets it, so it never
        // moves once a row exists, matching SyncEventQuarantine's original in-memory AddOrUpdate
        // semantics exactly. Only the counter matching THIS attempt's kind is incremented — a
        // deferred attempt must never advance the permanent counter and vice versa, since the two
        // budgets exist precisely so one kind can't exhaust the other's threshold.
        await conn.ExecuteAsync($@"
            INSERT INTO tbl_sync_quarantine
                (event_id, event_type, origin_node_id, failure_count, deferred_failure_count,
                 first_failed_at_utc, last_failed_at_utc, last_error, last_failure_kind)
            VALUES (@EventId, @EventType, @OriginNodeId,
                    CASE WHEN @Kind = 'permanent' THEN 1 ELSE 0 END,
                    CASE WHEN @Kind = 'deferred' THEN 1 ELSE 0 END,
                    @Now, @Now, @Error, @Kind)
            ON CONFLICT(event_id) DO UPDATE SET
                failure_count = failure_count + CASE WHEN @Kind = 'permanent' THEN 1 ELSE 0 END,
                deferred_failure_count = deferred_failure_count + CASE WHEN @Kind = 'deferred' THEN 1 ELSE 0 END,
                last_failed_at_utc = @Now,
                last_error = @Error,
                last_failure_kind = @Kind",
            new { EventId = eventId, EventType = eventType, OriginNodeId = originNodeId, Now = now, Error = error, Kind = kindText });

        var raw = await conn.QuerySingleAsync<dynamic>(
            $"SELECT {SelectColumns} FROM tbl_sync_quarantine WHERE event_id = @EventId",
            new { EventId = eventId });
        return ToEntry(raw);
    }

    public async Task ClearAsync(Guid eventId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_sync_quarantine WHERE event_id = @EventId",
            new { EventId = eventId });
    }

    public async Task<List<SyncQuarantineEntry>> GetAllAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"SELECT {SelectColumns} FROM tbl_sync_quarantine ORDER BY (failure_count + deferred_failure_count) DESC");
        return rows.Select(ToEntry).ToList();
    }

    // Dapper's constructor-based materialization into SyncQuarantineEntry directly needs a
    // constructor whose parameter types match the RAW column types Dapper reads (string/long),
    // not the mapped CLR types the registered Guid/DateTime TypeHandlers (DapperConfig) produce —
    // those handlers only get invoked for a strongly-typed property/parameter binding, and a
    // `dynamic` row has none. Querying into `dynamic` and converting by hand (mirroring
    // DapperConfig's own Guid/DateTime Parse logic — round-trip "o" format, Unspecified treated as
    // UTC) sidesteps that mismatch; same pattern RestoreEventStateRepository already uses for its
    // own record type (though it dodges the issue entirely by keeping its own DTO fields as raw
    // strings instead of Guid/DateTime).
    private static SyncQuarantineEntry ToEntry(dynamic raw) => new(
        EventId: Guid.Parse((string)raw.EventId),
        EventType: (string)raw.EventType,
        OriginNodeId: Guid.Parse((string)raw.OriginNodeId),
        PermanentFailureCount: (int)(long)raw.PermanentFailureCount,
        DeferredFailureCount: (int)(long)raw.DeferredFailureCount,
        FirstFailedAtUtc: ParseUtc((string)raw.FirstFailedAtUtc),
        LastFailedAtUtc: ParseUtc((string)raw.LastFailedAtUtc),
        LastError: (string)raw.LastError,
        LastFailureKind: ParseKind((string)raw.LastFailureKind));

    private static DateTime ParseUtc(string value)
    {
        var dt = DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
        return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
    }

    private static string ToKindText(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Deferred => "deferred",
        _ => "permanent"
    };

    private static SyncFailureKind ParseKind(string value) =>
        value == "deferred" ? SyncFailureKind.Deferred : SyncFailureKind.Permanent;
}

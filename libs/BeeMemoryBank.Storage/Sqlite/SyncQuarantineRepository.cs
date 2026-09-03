using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class SyncQuarantineRepository(DbConnectionFactory factory) : BaseRepository(factory), ISyncQuarantineRepository
{
    private const string SelectColumns = @"
        event_id            AS EventId,
        event_type          AS EventType,
        origin_node_id      AS OriginNodeId,
        failure_count       AS FailureCount,
        first_failed_at_utc AS FirstFailedAtUtc,
        last_failed_at_utc  AS LastFailedAtUtc,
        last_error          AS LastError";

    public async Task<SyncQuarantineEntry> RecordFailureAsync(Guid eventId, string eventType, Guid originNodeId, string error)
    {
        using var conn = OpenConnection();
        var now = DateTime.UtcNow;

        // Single atomic INSERT..ON CONFLICT instead of read-then-increment-then-write: the same
        // globally-unique EventId can be recorded as failed from more than one peer relay in short
        // succession (see the table's own comment on origin_node_id), and SQLite's single-writer
        // model makes this statement atomic without needing an app-level lock. FirstFailedAtUtc is
        // deliberately left OUT of the UPDATE clause — only the INSERT branch sets it, so it never
        // moves once a row exists, matching SyncEventQuarantine's original in-memory AddOrUpdate
        // semantics exactly.
        await conn.ExecuteAsync($@"
            INSERT INTO tbl_sync_quarantine
                (event_id, event_type, origin_node_id, failure_count, first_failed_at_utc, last_failed_at_utc, last_error)
            VALUES (@EventId, @EventType, @OriginNodeId, 1, @Now, @Now, @Error)
            ON CONFLICT(event_id) DO UPDATE SET
                failure_count = failure_count + 1,
                last_failed_at_utc = @Now,
                last_error = @Error",
            new { EventId = eventId, EventType = eventType, OriginNodeId = originNodeId, Now = now, Error = error });

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
            $"SELECT {SelectColumns} FROM tbl_sync_quarantine ORDER BY failure_count DESC");
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
        FailureCount: (int)(long)raw.FailureCount,
        FirstFailedAtUtc: ParseUtc((string)raw.FirstFailedAtUtc),
        LastFailedAtUtc: ParseUtc((string)raw.LastFailedAtUtc),
        LastError: (string)raw.LastError);

    private static DateTime ParseUtc(string value)
    {
        var dt = DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
        return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
    }
}

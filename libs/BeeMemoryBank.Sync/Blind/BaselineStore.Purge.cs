using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

public sealed partial class BaselineStore
{
    /// <summary>
    /// The tables that may hold rows on an activated blind host (spec 9.1; golden
    /// <c>docs/blind-node/OPERATIONAL-TABLES.golden.txt</c>): schema bookkeeping, the node's identity, peers and positions, its
    /// event log (sealed and control rows only), the blob store, its own state, the recovery and key material it keeps
    /// without being able to open it, the baseline store, the restore and rotation records, and its own audit log. Every
    /// other table exists (the migrations are shared) and is empty after the first adoption.
    /// </summary>
    public static readonly IReadOnlyList<string> OperationalTables =
    [
        "sqlite_sequence", "tbl_audit_log", "tbl_baseline", "tbl_baseline_blob", "tbl_blind_restore_code", "tbl_blind_state",
        "tbl_blob", "tbl_compaction_log", "tbl_dek_retired_link", "tbl_dek_rotation_state", "tbl_event", "tbl_metadata_activation",
        "tbl_metadata_key", "tbl_migration", "tbl_migration_marker", "tbl_node_identity", "tbl_recovery_box",
        "tbl_recovery_box_check", "tbl_recovery_box_pending_retire", "tbl_restore_event_state", "tbl_restore_replay_shield",
        "tbl_sealed_secret", "tbl_state_anchor", "tbl_sync_position", "tbl_sync_push_position", "tbl_sync_quarantine",
        "tbl_whitelist",
    ];

    /// <summary>
    /// <c>wal_checkpoint(TRUNCATE)</c> reports a checkpoint that could not finish (a reader still pins old pages) in its result
    /// row, not as an error; the old pages are then still in the WAL, so that is a failed purge.
    /// </summary>
    private static async Task TruncateWalAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var result = await cmd.ExecuteReaderAsync(ct);
        if (await result.ReadAsync(ct) && result.GetInt64(0) != 0)
            throw new IOException("The write-ahead log could not be truncated (a reader still uses it); the legacy content may still be in it.");
    }

    public async Task PurgeLegacyContentAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PurgeLockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Spec 9.3: every row of every table that is not operational goes, with <c>secure_delete</c> on so the freed pages are
    /// zeroed; every protocol-1..3 content row of the log goes (and the quarantine rows of such events, whose error texts may
    /// quote a path); the full-text indexes are emptied; then the file is rebuilt (<c>VACUUM</c>) and the WAL truncated, so
    /// none of the old pages survives in the database file or beside it; only after both succeed is <c>opaque_store</c> set. Idempotent:
    /// a second run finds nothing to delete, and a run that was cut short (no flag) is simply done again.
    /// The copies of the database an older build or a backup kept are not touched; the status reports them.
    /// </summary>
    private async Task PurgeLockedAsync(CancellationToken ct)
    {
        using var writes = await EventWriteGate.Instance.EnterAsync(ct);
        using var conn = (SqliteConnection)factory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        await conn.ExecuteAsync("PRAGMA secure_delete = ON");
        await conn.ExecuteAsync("PRAGMA foreign_keys = OFF");
        try
        {
            var schema = (await conn.QueryAsync<(string Name, string? Sql)>(
                "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name")).ToList();
            var virtualTables = schema
                .Where(t => t.Sql is { } sql && sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name).ToList();
            var operational = new HashSet<string>(OperationalTables, StringComparer.Ordinal);
            var removed = 0;

            using (var tx = conn.BeginTransaction())
            {
                foreach (var (name, _) in schema)
                {
                    ct.ThrowIfCancellationRequested();
                    if (operational.Contains(name) || name.StartsWith("sqlite_", StringComparison.Ordinal)) continue;
                    if (virtualTables.Contains(name)) continue;
                    if (virtualTables.Any(v => name.StartsWith(v + "_", StringComparison.Ordinal))) continue;  // a full-text shadow table
                    removed += await conn.ExecuteAsync($"DELETE FROM \"{name.Replace("\"", "\"\"")}\"", transaction: tx);
                }
                // The full-text indexes over titles, paths and tag names: emptied through their own command.
                foreach (var fts in virtualTables)
                    await conn.ExecuteAsync($"INSERT INTO \"{fts}\"(\"{fts}\") VALUES ('delete-all')", transaction: tx);

                var sealable = EventClasses.Sealable.ToArray();
                var contentEvents = await conn.QueryAsync<string>(
                    "SELECT event_id FROM tbl_event WHERE protocol_version < @p AND event_type IN @sealable",
                    new { p = EventClasses.SealedProtocol, sealable }, tx);
                removed += await conn.ExecuteAsync(
                    "DELETE FROM tbl_event WHERE protocol_version < @p AND event_type IN @sealable",
                    new { p = EventClasses.SealedProtocol, sealable }, tx);
                removed += await conn.ExecuteAsync(
                    "DELETE FROM tbl_sync_quarantine WHERE event_type IN @sealable", new { sealable }, tx);
                tx.Commit();
                _logger.LogInformation("Blind store: legacy content deleted ({Rows} rows, {Events} clear content events); rebuilding the file",
                    removed, contentEvents.Count());
            }

            // The rows are gone from the tables, not yet from the pages: the file is rebuilt and the WAL truncated first, and only
            // then does the node say its store is opaque. A failure (no disk for VACUUM, a reader pinning the WAL, a crash) leaves
            // the flag unset, so the next adoption and the log trimmer's next run do this again.
            await conn.ExecuteAsync("VACUUM");
            await TruncateWalAsync(conn, ct);
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@OpaqueStoreKey, '1', @now)
                  ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                new { OpaqueStoreKey, now = _time.GetUtcNow().UtcDateTime.ToString("O") });
            _logger.LogInformation("Blind store: legacy content purged");
        }
        finally
        {
            await conn.ExecuteAsync("PRAGMA foreign_keys = ON");
            await conn.ExecuteAsync("PRAGMA secure_delete = OFF");
        }
    }
}

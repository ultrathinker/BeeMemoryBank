using System.Data;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// The lowest Lamport time this node continues from, kept on disk for a node that holds content but no event carrying
/// that time: one that joined from a snapshot, or was restored (the event log is not part of either). At every start
/// the clock is initialized from the event log, and an event-less node used to come back at 0 and write its first
/// edits below the Lamport times of the rows it imported, so they lost last-writer-wins at every peer.
/// <see cref="EventLogRepository.GetMaxLamportTimestampAsync"/> therefore answers max(event log, floor).
///
/// <para>Kept in <c>tbl_migration_marker</c>: local bookkeeping, never imported over the local rows (see
/// <see cref="SnapshotTables.SchemaMeta"/>). It only ever rises.</para>
/// </summary>
public static class LamportFloor
{
    public const string Key = "lamport_floor";

    internal const string SelectSql =
        "SELECT COALESCE((SELECT CAST(value AS INTEGER) FROM tbl_migration_marker WHERE key = '" + Key + "'), 0)";

    /// <summary>Raises the floor to <paramref name="lamportTs"/>; a lower value leaves it as it is.</summary>
    public static void Raise(IDbConnection conn, IDbTransaction? tx, long lamportTs)
    {
        conn.Execute(
            @"INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, @Value, @Now)
              ON CONFLICT(key) DO UPDATE SET value = excluded.value, set_at = excluded.set_at
              WHERE CAST(excluded.value AS INTEGER) > CAST(tbl_migration_marker.value AS INTEGER)",
            new { Key, Value = lamportTs.ToString(System.Globalization.CultureInfo.InvariantCulture), Now = DateTime.UtcNow.ToString("O") },
            tx);
    }

    /// <inheritdoc cref="Raise(IDbConnection, IDbTransaction?, long)"/>
    public static void Raise(IDbConnectionFactory db, long lamportTs)
    {
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        Raise(conn, null, lamportTs);
    }

    public static long Read(IDbConnection conn, IDbTransaction? tx = null) =>
        conn.ExecuteScalar<long>(SelectSql, transaction: tx);
}

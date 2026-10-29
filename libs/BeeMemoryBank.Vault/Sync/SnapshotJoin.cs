using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// What every join does once the snapshot is in (the Setup page's join and <c>bmb join</c>), in one place: the pull
/// position at the snapshot's checkpoint, the Lamport clock past the imported rows, and the initial sync marked done.
/// Without the position the first sync asks the host for its log from 0, which a host that ever compacted answers
/// with 410 forever (BMB-81).
/// </summary>
public static class SnapshotJoin
{
    /// <summary>
    /// How far a snapshot may move this node's clock at once: a host's Lamport time is its own word, and a hostile one
    /// could otherwise push this node's clock to the top of the range.
    /// </summary>
    public const long MaxClockAdvance = 1_000_000;

    /// <summary>Records the join; returns the Lamport time the clock was advanced to.</summary>
    public static async Task<long> CompleteAsync(
        Guid hostNodeId,
        long cpSeq,
        long lamportTs,
        ISyncPositionRepository syncPositions,
        ILamportClock clock,
        INodeIdentityRepository nodeRepo,
        IDbConnectionFactory db,
        ILogger logger)
    {
        await syncPositions.UpsertAsync(new SyncPosition
        {
            RemoteNodeId = hostNodeId,
            LastSequenceNum = cpSeq,
            UpdatedAt = DateTime.UtcNow
        });

        var capped = Math.Min(lamportTs, clock.Current + MaxClockAdvance);
        if (lamportTs > capped)
            logger.LogWarning(
                "Producer lamport_ts {Producer} exceeds local+MAX_CLOCK_ADVANCE, capping at {Capped}.",
                lamportTs, capped);
        clock.Update(capped);
        // The cap above bounds the manifest's unsigned claim; it must not leave this node below the rows it has just taken over: a vault whose Lamport time is past
        // the cap would otherwise stamp this node's first edits lower than the imported versions, and they would lose last-writer-wins at every peer.
        var imported = MaxImportedLamport(db);
        if (imported is < 0 or >= MaxAcceptedLamport)
            throw new InvalidDataException($"The snapshot holds rows stamped with Lamport time {imported}, beyond the {MaxAcceptedLamport} a join accepts; refusing it.");
        if (imported > clock.Current) clock.RaiseTo(imported);
        // Durable: the event log holds nothing of what was imported, and the clock is rebuilt from it at every start.
        LamportFloor.Raise(db, clock.Current);

        await nodeRepo.MarkInitialSyncCompletedAsync();
        return clock.Current;
    }

    /// <summary>The highest Lamport time a row of an imported snapshot may carry (2^53, as for a restore); a snapshot above it is refused.</summary>
    public const long MaxAcceptedLamport = 1L << 53;

    // Every Lamport column of the replicated state a join imports (a snapshot of an older schema may lack some). The same set the recovery restore reads
    // (RecoveryRestoreService.LamportColumns), without the whitelist: a join writes its own whitelist rows here.
    private static readonly (string Table, string Column)[] LamportColumns =
    [
        ("tbl_article", "lamport_ts"), ("tbl_folder", "lamport_ts"), ("tbl_media", "lamport_ts"),
        ("tbl_comment", "lamport_ts"), ("tbl_comment", "delete_lamport_ts"), ("tbl_tombstone", "lamport_ts"),
        ("tbl_conflict_version", "lamport_ts"), ("tbl_recovery_box", "lamport_ts"),
        ("tbl_state_anchor", "lamport_ts"), ("tbl_sealed_secret", "lamport_ts"),
    ];

    /// <summary>The highest Lamport time of the rows now in the replicated tables; 0 for none.</summary>
    public static long MaxImportedLamport(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var tables = TableNames(conn);
        long max = 0;
        foreach (var (table, column) in LamportColumns)
        {
            if (!tables.Contains(table)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT MAX({column}) FROM {table}";
            if (cmd.ExecuteScalar() is long value) max = Math.Max(max, value);
        }
        return max;
    }

    private static HashSet<string> TableNames(System.Data.IDbConnection conn)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var list = conn.CreateCommand();
        list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = list.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>
    /// Takes a node a failed join left half-made back to "not initialized", so the join can simply be run again instead
    /// of hitting "already initialized". The rows the join itself writes before the snapshot, and the replicated tables
    /// (<see cref="SnapshotTables.Replicated"/>) with the hard-delete audit: the table import commits before the steps that
    /// follow it (media copy, clean-up, the position), so a failure there leaves the imported content behind, and a retry
    /// that imports a newer snapshot over it with INSERT OR IGNORE would keep the older version of every row that changed
    /// in between. Media files are not touched here; the next import's orphan clean-up owns them. All in one transaction,
    /// so a crash here cannot leave half of them.
    /// </summary>
    public static void RollbackPartialNode(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var present = TableNames(conn);
        // The import itself runs with foreign keys off, and the tables reference each other in both directions; the empty database is consistent either way.
        using (var off = conn.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF"; off.ExecuteNonQuery(); }
        try
        {
            using var tx = conn.BeginTransaction();
            // Content first, in the reverse of the import order (tbl_blob, which the bodies address, goes last); then the node's own rows in FK order: tbl_user references tbl_key_slot.
            foreach (var table in SnapshotTables.Replicated.Reverse().Concat(["tbl_hard_delete_audit"]))
            {
                if (!present.Contains(table)) continue;
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {table}";
                del.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM tbl_sync_position;
                DELETE FROM tbl_whitelist;
                DELETE FROM tbl_user;
                DELETE FROM tbl_key_slot;
                DELETE FROM tbl_node_identity;";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        finally
        {
            using var on = conn.CreateCommand();
            on.CommandText = "PRAGMA foreign_keys = ON";
            on.ExecuteNonQuery();
        }
    }
}

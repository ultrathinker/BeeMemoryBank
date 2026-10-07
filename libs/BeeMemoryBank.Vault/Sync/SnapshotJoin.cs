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
        // Durable: the event log holds nothing of what was imported, and the clock is rebuilt from it at every start.
        LamportFloor.Raise(db, clock.Current);

        await nodeRepo.MarkInitialSyncCompletedAsync();
        return capped;
    }

    /// <summary>
    /// Takes a node a failed join left half-made back to "not initialized", so the join can simply be run again instead
    /// of hitting "already initialized". Only the rows the join itself writes before the snapshot: the snapshot's table
    /// import is one transaction, so a failed import committed nothing. All in one transaction, so a crash here cannot
    /// leave half of them.
    /// </summary>
    public static void RollbackPartialNode(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // FK order: tbl_user references tbl_key_slot.
        cmd.CommandText = @"
            DELETE FROM tbl_sync_position;
            DELETE FROM tbl_whitelist;
            DELETE FROM tbl_user;
            DELETE FROM tbl_key_slot;
            DELETE FROM tbl_node_identity;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}

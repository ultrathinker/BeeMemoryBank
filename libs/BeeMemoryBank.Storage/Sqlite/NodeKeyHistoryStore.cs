using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>A node-local identity conflict (plan WS-D D2): two different valid rotations of one node from the same old key and counter.</summary>
public sealed record NodeKeyConflict(
    Guid NodeId, long Counter, byte[] OldPublicKey, Guid FirstEventId, byte[] FirstNewKey, Guid SecondEventId, byte[] SecondNewKey,
    string DetectedAt, Guid? SettledEventId, string? SettledAt)
{
    public bool IsOpen => SettledEventId is null;
}

/// <summary>
/// <c>tbl_node_key_history</c> and <c>tbl_node_key_conflict</c> (migration 049): the retired identity keys of every node, the counter of a node's
/// key (the highest counter of its rows, 0 without any), the key transition chain, and the identity conflicts. Tolerates a database before
/// migration 049 (no rows, counter 0), so a host that reads an old file never fails here.
/// </summary>
public sealed class NodeKeyHistoryStore(IDbConnectionFactory factory)
{
    /// <summary>Rows kept per node (plan D1): older ones are pruned first.</summary>
    public const int MaxRowsPerNode = NodeKeyTransition.MaxChain;

    private const string Columns = @"node_id AS NodeId, counter AS Counter, public_key AS PublicKey, new_public_key AS NewPublicKey,
        valid_until_lamport AS ValidUntilLamport, rotation_event_id AS RotationEventId, checkpoint_lamport_ts AS CheckpointLamportTs,
        own_event_ids AS OwnEventIds, old_key_proof AS OldKeyProof, new_key_proof AS NewKeyProof";

    private sealed class Row
    {
        public string NodeId { get; set; } = "";
        public long Counter { get; set; }
        public byte[] PublicKey { get; set; } = [];
        public byte[] NewPublicKey { get; set; } = [];
        public long ValidUntilLamport { get; set; }
        public string RotationEventId { get; set; } = "";
        public long CheckpointLamportTs { get; set; }
        public string OwnEventIds { get; set; } = "";
        public byte[] OldKeyProof { get; set; } = [];
        public byte[] NewKeyProof { get; set; } = [];

        public NodeKeyHistoryEntry ToEntry() => new(Guid.Parse(NodeId), Counter, PublicKey, NewPublicKey, ValidUntilLamport,
            Guid.Parse(RotationEventId), CheckpointLamportTs, OwnEventIds, OldKeyProof, NewKeyProof);
    }

    private static string Id(Guid id) => id.ToString("D");

    /// <summary>True when the schema has the history (migration 049 ran).</summary>
    public static bool HasHistory(IDbConnection connection, IDbTransaction? transaction = null) =>
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_node_key_history'", transaction: transaction) > 0;

    /// <summary>Every row of <paramref name="nodeId"/>, oldest counter first.</summary>
    public async Task<IReadOnlyList<NodeKeyHistoryEntry>> ListAsync(Guid nodeId)
    {
        using var conn = factory.CreateConnection();
        return await ListAsync(conn, null, nodeId);
    }

    public static async Task<IReadOnlyList<NodeKeyHistoryEntry>> ListAsync(IDbConnection conn, IDbTransaction? tx, Guid nodeId)
    {
        if (!HasHistory(conn, tx)) return [];
        return (await conn.QueryAsync<Row>(
                $"SELECT {Columns} FROM tbl_node_key_history WHERE node_id = @id COLLATE NOCASE ORDER BY counter", new { id = Id(nodeId) }, tx))
            .Select(r => r.ToEntry()).ToList();
    }

    /// <summary>Every row of every node (a package or snapshot carries them all).</summary>
    public static async Task<IReadOnlyList<NodeKeyHistoryEntry>> ListAllAsync(IDbConnection conn, IDbTransaction? tx = null)
    {
        if (!HasHistory(conn, tx)) return [];
        return (await conn.QueryAsync<Row>($"SELECT {Columns} FROM tbl_node_key_history ORDER BY node_id, counter", transaction: tx))
            .Select(r => r.ToEntry()).ToList();
    }

    /// <summary>The counter of the node's current key: the highest counter of its rows, 0 without any.</summary>
    public async Task<long> CounterAsync(Guid nodeId)
    {
        using var conn = factory.CreateConnection();
        return await CounterAsync(conn, null, nodeId);
    }

    public static async Task<long> CounterAsync(IDbConnection conn, IDbTransaction? tx, Guid nodeId) =>
        HasHistory(conn, tx)
            ? await conn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(counter), 0) FROM tbl_node_key_history WHERE node_id = @id COLLATE NOCASE",
                new { id = Id(nodeId) }, tx)
            : 0;

    /// <summary>The row a rotation event wrote, or null.</summary>
    public async Task<NodeKeyHistoryEntry?> GetByRotationEventAsync(Guid rotationEventId)
    {
        using var conn = factory.CreateConnection();
        if (!HasHistory(conn)) return null;
        return (await conn.QueryFirstOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM tbl_node_key_history WHERE rotation_event_id = @id COLLATE NOCASE", new { id = Id(rotationEventId) }))?.ToEntry();
    }

    /// <summary>Adds a row unless one with its node and counter is there (a re-delivery, a merge). True when it was added.</summary>
    public static async Task<bool> InsertIfAbsentAsync(IDbConnection conn, IDbTransaction? tx, NodeKeyHistoryEntry entry) =>
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_node_key_history
                (node_id, counter, public_key, new_public_key, valid_until_lamport, rotation_event_id, checkpoint_lamport_ts, own_event_ids,
                 old_key_proof, new_key_proof, created_at)
              VALUES (@NodeId, @Counter, @PublicKey, @NewPublicKey, @ValidUntilLamport, @RotationEventId, @CheckpointLamportTs, @OwnEventIds,
                 @OldKeyProof, @NewKeyProof, @Now)",
            new
            {
                NodeId = Id(entry.NodeId), entry.Counter, entry.PublicKey, entry.NewPublicKey, entry.ValidUntilLamport,
                RotationEventId = Id(entry.RotationEventId), entry.CheckpointLamportTs, entry.OwnEventIds, entry.OldKeyProof, entry.NewKeyProof,
                Now = DateTime.UtcNow.ToString("O"),
            }, tx) > 0;

    /// <summary>The newest <see cref="NodeKeyTransition.MaxChain"/> links of the node, oldest first (what the handshake returns, plan D4).</summary>
    public async Task<IReadOnlyList<NodeKeyLink>> ChainAsync(Guid nodeId)
    {
        var rows = await ListAsync(nodeId);
        return rows.Skip(Math.Max(0, rows.Count - NodeKeyTransition.MaxChain)).Select(r => r.Link).ToList();
    }

    /// <summary>
    /// Drops the rows no longer needed (plan D1): those whose retired key could only have signed events below <paramref name="oldestServedLamport"/>
    /// (the compaction checkpoint: nothing this node still serves needs them), and all but the newest <see cref="MaxRowsPerNode"/> of each node.
    /// The newest row of a node is always kept: it carries the node's counter.
    /// </summary>
    public static async Task<int> PruneAsync(IDbConnection conn, IDbTransaction? tx, long oldestServedLamport)
    {
        if (!HasHistory(conn, tx)) return 0;
        var removed = await conn.ExecuteAsync(
            @"DELETE FROM tbl_node_key_history
               WHERE valid_until_lamport < @oldest
                 AND counter < (SELECT MAX(h.counter) FROM tbl_node_key_history h WHERE h.node_id = tbl_node_key_history.node_id)",
            new { oldest = oldestServedLamport }, tx);
        removed += await conn.ExecuteAsync(
            @"DELETE FROM tbl_node_key_history
               WHERE counter <= (SELECT MAX(h.counter) FROM tbl_node_key_history h WHERE h.node_id = tbl_node_key_history.node_id) - @keep",
            new { keep = MaxRowsPerNode }, tx);
        return removed;
    }

    /// <summary>
    /// What compaction drops (release 2.12, plan WS-D D1): all but the newest <see cref="MaxRowsPerNode"/> rows of each node. Not by Lamport time: a
    /// device that was away walks the newest 16 links from the key it pinned, and is promised that it pairs again only after more than 16 re-keys.
    /// </summary>
    public static Task<int> PruneToCapAsync(IDbConnection conn, IDbTransaction? tx) => PruneAsync(conn, tx, long.MinValue);

    // ---- conflicts -----------------------------------------------------------------------------------------------------------

    private sealed class ConflictRow
    {
        public string NodeId { get; set; } = "";
        public long Counter { get; set; }
        public byte[] OldPublicKey { get; set; } = [];
        public string FirstEventId { get; set; } = "";
        public byte[] FirstNewKey { get; set; } = [];
        public string SecondEventId { get; set; } = "";
        public byte[] SecondNewKey { get; set; } = [];
        public string DetectedAt { get; set; } = "";
        public string? SettledEventId { get; set; }
        public string? SettledAt { get; set; }
    }

    private static bool HasConflicts(IDbConnection conn, IDbTransaction? tx) =>
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_node_key_conflict'", transaction: tx) > 0;

    public async Task<NodeKeyConflict?> GetConflictAsync(Guid nodeId)
    {
        using var conn = factory.CreateConnection();
        return await GetConflictAsync(conn, null, nodeId);
    }

    public static async Task<NodeKeyConflict?> GetConflictAsync(IDbConnection conn, IDbTransaction? tx, Guid nodeId)
    {
        if (!HasConflicts(conn, tx)) return null;
        var row = await conn.QueryFirstOrDefaultAsync<ConflictRow>(
            @"SELECT node_id AS NodeId, counter AS Counter, old_public_key AS OldPublicKey, first_event_id AS FirstEventId, first_new_key AS FirstNewKey,
                     second_event_id AS SecondEventId, second_new_key AS SecondNewKey, detected_at AS DetectedAt, settled_event_id AS SettledEventId,
                     settled_at AS SettledAt
                FROM tbl_node_key_conflict WHERE node_id = @id COLLATE NOCASE", new { id = Id(nodeId) }, tx);
        return row is null ? null : new NodeKeyConflict(Guid.Parse(row.NodeId), row.Counter, row.OldPublicKey, Guid.Parse(row.FirstEventId),
            row.FirstNewKey, Guid.Parse(row.SecondEventId), row.SecondNewKey, row.DetectedAt,
            row.SettledEventId is null ? null : Guid.Parse(row.SettledEventId), row.SettledAt);
    }

    /// <summary>Every open conflict (the Devices page and the network map show them).</summary>
    public async Task<IReadOnlyList<NodeKeyConflict>> ListOpenConflictsAsync()
    {
        using var conn = factory.CreateConnection();
        if (!HasConflicts(conn, null)) return [];
        var ids = await conn.QueryAsync<string>("SELECT node_id FROM tbl_node_key_conflict WHERE settled_event_id IS NULL ORDER BY detected_at");
        var list = new List<NodeKeyConflict>();
        foreach (var id in ids)
            if (await GetConflictAsync(conn, null, Guid.Parse(id)) is { } conflict) list.Add(conflict);
        return list;
    }

    /// <summary>Records an open conflict (replacing a settled one of the same node).</summary>
    public static Task RecordConflictAsync(IDbConnection conn, IDbTransaction? tx, Guid nodeId, long counter, byte[] oldPublicKey,
        Guid firstEventId, byte[] firstNewKey, Guid secondEventId, byte[] secondNewKey) =>
        conn.ExecuteAsync(
            @"INSERT INTO tbl_node_key_conflict
                (node_id, counter, old_public_key, first_event_id, first_new_key, second_event_id, second_new_key, detected_at, settled_event_id, settled_at)
              VALUES (@id, @counter, @old, @first, @firstKey, @second, @secondKey, @now, NULL, NULL)
              ON CONFLICT(node_id) DO UPDATE SET counter = excluded.counter, old_public_key = excluded.old_public_key,
                first_event_id = excluded.first_event_id, first_new_key = excluded.first_new_key, second_event_id = excluded.second_event_id,
                second_new_key = excluded.second_new_key, detected_at = excluded.detected_at, settled_event_id = NULL, settled_at = NULL
              WHERE tbl_node_key_conflict.settled_event_id IS NOT NULL",
            new
            {
                id = Id(nodeId), counter, old = oldPublicKey, first = Id(firstEventId), firstKey = firstNewKey, second = Id(secondEventId),
                secondKey = secondNewKey, now = DateTime.UtcNow.ToString("O"),
            }, tx);

    public static Task SettleConflictAsync(IDbConnection conn, IDbTransaction? tx, Guid nodeId, Guid confirmEventId) =>
        conn.ExecuteAsync(
            "UPDATE tbl_node_key_conflict SET settled_event_id = @e, settled_at = @now WHERE node_id = @id COLLATE NOCASE AND settled_event_id IS NULL",
            new { id = Id(nodeId), e = Id(confirmEventId), now = DateTime.UtcNow.ToString("O") }, tx);
}

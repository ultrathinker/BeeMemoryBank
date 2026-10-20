using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class EventLogRepository(DbConnectionFactory factory) : BaseRepository(factory), IEventLogRepository
{
    private const string SelectCols = @"
        sequence_num    AS SequenceNum,
        event_id        AS EventId,
        node_id         AS NodeId,
        lamport_ts      AS LamportTs,
        event_type      AS EventType,
        article_id      AS ArticleId,
        entity_id       AS EntityId,
        payload         AS Payload,
        signature       AS Signature,
        protocol_version AS ProtocolVersion,
        created_at      AS CreatedAt,
        actor_type      AS ActorType,
        actor_name      AS ActorName,
        via_agent_name  AS ViaAgentName";

    // What a peer is sent (step-a-spec 3.2, 6.1): the original columns, and for a protocol-4 row no entity id and no actor
    // fields. They are not signed, and on a sealed row they would name what the envelope hides (a folder path is an
    // entity id). The local columns of migration 038 are never selected here.
    private const string WireSelectCols = @"
        sequence_num    AS SequenceNum,
        event_id        AS EventId,
        node_id         AS NodeId,
        lamport_ts      AS LamportTs,
        event_type      AS EventType,
        article_id      AS ArticleId,
        CASE WHEN protocol_version >= 4 THEN NULL ELSE entity_id END      AS EntityId,
        payload         AS Payload,
        signature       AS Signature,
        protocol_version AS ProtocolVersion,
        created_at      AS CreatedAt,
        CASE WHEN protocol_version >= 4 THEN NULL ELSE actor_type END     AS ActorType,
        CASE WHEN protocol_version >= 4 THEN NULL ELSE actor_name END     AS ActorName,
        CASE WHEN protocol_version >= 4 THEN NULL ELSE via_agent_name END AS ViaAgentName";

    // What this node's own activity views read (spec 6.1): a sealed row shows the type and article its opened view had.
    private const string ActivitySelectCols = @"
        sequence_num    AS SequenceNum,
        event_id        AS EventId,
        node_id         AS NodeId,
        lamport_ts      AS LamportTs,
        COALESCE(local_type, event_type)       AS EventType,
        COALESCE(local_article_id, article_id) AS ArticleId,
        entity_id       AS EntityId,
        payload         AS Payload,
        signature       AS Signature,
        protocol_version AS ProtocolVersion,
        created_at      AS CreatedAt,
        actor_type      AS ActorType,
        actor_name      AS ActorName,
        via_agent_name  AS ViaAgentName";

    private const string InsertColumns = @"
        (event_id, node_id, lamport_ts, event_type, article_id, entity_id, payload, signature, protocol_version, created_at, actor_type, actor_name, via_agent_name,
         apply_state, hold_reason, held_at, origin_superadmin, local_type, local_article_id, origin_peer, origin_seq)
        VALUES (@EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @EntityId, @Payload, @Signature, @ProtocolVersion, @CreatedAt, @ActorType, @ActorName, @ViaAgentName,
         @ApplyState, @HoldReason, @HeldAt, @OriginSuperadmin, @LocalType, @LocalArticleId, @OriginPeer, @OriginSeq)";

    private static object Row(SyncEvent evt, EventRowLocal? local)
    {
        local ??= new EventRowLocal();
        return new
        {
            evt.EventId, evt.NodeId, evt.LamportTs, evt.EventType, evt.ArticleId, evt.EntityId, evt.Payload, evt.Signature,
            evt.ProtocolVersion, evt.CreatedAt, evt.ActorType, evt.ActorName, evt.ViaAgentName,
            local.ApplyState, local.HoldReason,
            HeldAt = local.ApplyState == EventApplyStates.Held ? DateTime.UtcNow.ToString("O") : null,
            OriginSuperadmin = local.OriginSuperadmin is { } sa ? (sa ? 1 : 0) : (int?)null,
            local.LocalType, local.LocalArticleId, local.OriginPeer, local.OriginSeq,
        };
    }

    public async Task AppendAsync(SyncEvent evt, System.Data.IDbTransaction? transaction = null, EventRowLocal? local = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            await conn.ExecuteAsync("INSERT INTO tbl_event " + InsertColumns, Row(evt, local), transaction);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

    public async Task<bool> AppendIfNotExistsAsync(SyncEvent evt, EventRowLocal? local = null)
    {
        using var conn = OpenConnection();
        var rows = await conn.ExecuteAsync("INSERT OR IGNORE INTO tbl_event " + InsertColumns, Row(evt, local));
        return rows > 0;
    }

    public async Task<bool> ExistsAsync(Guid eventId)
    {
        using var conn = OpenConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM tbl_event WHERE event_id = @eventId",
            new { eventId });
        return count > 0;
    }

    public async Task<long> GetMaxLamportTimestampAsync()
    {
        using var conn = OpenConnection();
        // The clock's starting point: the newest event, or the floor a snapshot join or a restore left
        // when there is no event to carry it (LamportFloor).
        return await conn.ExecuteScalarAsync<long>(
            $"SELECT MAX(COALESCE((SELECT MAX(lamport_ts) FROM tbl_event), 0), ({LamportFloor.SelectSql}))");
    }

    public async Task<List<SyncEvent>> GetAfterSequenceAsync(long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {WireSelectCols} FROM tbl_event WHERE sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { afterSequenceNum, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0, string? eventType = null)
    {
        using var conn = OpenConnection();
        // Filter by event_type in SQL so LIMIT counts only matching rows.
        // No dedicated index on event_type — fine at current log sizes; add one if the log grows large.
        var where = string.IsNullOrEmpty(eventType) ? "" : "WHERE COALESCE(local_type, event_type) = @eventType ";
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {ActivitySelectCols} FROM tbl_event {where}ORDER BY sequence_num DESC LIMIT @limit OFFSET @offset",
            new { limit, offset, eventType })).ToList();
    }

    public async Task<int> GetTotalCountAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM tbl_event");
    }

    public async Task<List<SyncEvent>> GetByArticleAsync(Guid articleId, int limit = 50)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {ActivitySelectCols} FROM tbl_event WHERE COALESCE(local_article_id, article_id) = @articleId ORDER BY sequence_num DESC LIMIT @limit",
            new { articleId, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetAllAfterSequenceAsync(long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {WireSelectCols} FROM tbl_event WHERE sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { afterSequenceNum, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetLocalEventsAfterSequenceAsync(Guid nodeId, long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {WireSelectCols} FROM tbl_event WHERE node_id = @nodeId AND sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { nodeId, afterSequenceNum, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetEventsToRelayAsync(Guid excludeNodeId, long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {WireSelectCols} FROM tbl_event WHERE node_id != @excludeNodeId AND sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { excludeNodeId, afterSequenceNum, limit })).ToList();
    }

    public async Task<bool> IsHardDeletedAsync(string entityId, long lamportTs)
    {
        // Query tbl_hard_delete_audit (NOT tbl_event) so the gate survives compaction —
        // tbl_event hard_delete rows get purged by CompactionService, but the audit
        // table is never compacted.
        using var conn = OpenConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM tbl_hard_delete_audit WHERE entity_identifier = @entityId AND lamport_ts >= @lamportTs",
            new { entityId, lamportTs });
        return count > 0;
    }

    public async Task<long?> GetMinSequenceAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long?>(
            "SELECT MIN(sequence_num) FROM tbl_event");
    }

    public async Task<long> GetMaxSequenceAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(sequence_num), 0) FROM tbl_event");
    }

    public async Task<int> DeletePlaintextCommentEventsAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"DELETE FROM tbl_event
               WHERE event_type = 'comment_create'
                 AND json_valid(payload)
                 AND COALESCE(json_extract(payload, '$.encrypted'), 0) = 0
                 AND COALESCE(json_extract(payload, '$.text'), '') <> ''");
    }

    public async Task<int> DeleteUpToAsync(long cpSequenceNum)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM tbl_event WHERE sequence_num <= @cpSequenceNum",
            new { cpSequenceNum });
    }

    public async Task<long?> GetLastCompactionCpAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long?>(
            "SELECT MAX(cp_after) FROM tbl_compaction_log");
    }

    public async Task<long?> GetSequenceAtRankAsync(int rank)
    {
        if (rank < 1) return null;
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long?>(
            "SELECT sequence_num FROM tbl_event ORDER BY sequence_num ASC LIMIT 1 OFFSET @offset",
            new { offset = rank - 1 });
    }

    public async Task<int> CountEventsAfterSequenceAsync(long seqNum)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_event WHERE sequence_num > @seqNum",
            new { seqNum });
    }

    public async Task<int> CountEventsToRelayAfterAsync(Guid excludeNodeId, long afterSequenceNum)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_event WHERE node_id != @excludeNodeId AND sequence_num > @afterSequenceNum",
            new { excludeNodeId, afterSequenceNum });
    }

    public async Task<SyncEvent?> GetByIdAsync(string eventId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event WHERE event_id = @eventId COLLATE NOCASE",
            new { eventId });
    }

    public async Task<bool> AnyHeldAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            $"SELECT EXISTS (SELECT 1 FROM tbl_event WHERE apply_state = {EventApplyStates.Held})") == 1;
    }

    public async Task<List<long>> GetHeldSequencesAsync(long afterSequenceNum, int limit)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<long>(
            $"SELECT sequence_num FROM tbl_event WHERE apply_state = {EventApplyStates.Held} AND sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { afterSequenceNum, limit })).ToList();
    }

    public async Task<HeldEventRow?> GetHeldAsync(long sequenceNum)
    {
        using var conn = OpenConnection();
        var row = (await conn.QueryAsync<HeldRowDto>(
            $@"SELECT {SelectCols}, hold_reason AS HoldReason, origin_superadmin AS OriginSuperadmin, held_at AS HeldAt,
                      origin_peer AS OriginPeer, origin_seq AS OriginSeq
               FROM tbl_event WHERE sequence_num = @sequenceNum AND apply_state = {EventApplyStates.Held}",
            new { sequenceNum })).FirstOrDefault();
        if (row is null) return null;
        var evt = new SyncEvent
        {
            SequenceNum = row.SequenceNum, EventId = row.EventId, NodeId = row.NodeId, LamportTs = row.LamportTs,
            EventType = row.EventType, ArticleId = row.ArticleId, EntityId = row.EntityId, Payload = row.Payload,
            Signature = row.Signature, ProtocolVersion = row.ProtocolVersion, CreatedAt = row.CreatedAt,
            ActorType = row.ActorType, ActorName = row.ActorName, ViaAgentName = row.ViaAgentName,
        };
        DateTime? heldAt = DateTime.TryParse(row.HeldAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;
        Guid? originPeer = Guid.TryParse(row.OriginPeer, out var peer) ? peer : null;
        return new HeldEventRow(evt, row.HoldReason, row.OriginSuperadmin == 1, heldAt, originPeer, row.OriginSeq);
    }

    public async Task MarkHeldAppliedAsync(long sequenceNum, string? localType, Guid? localArticleId, string? entityId, string? actorType, string? actorName)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            $@"UPDATE tbl_event SET apply_state = {EventApplyStates.Applied}, hold_reason = NULL, held_at = NULL,
                      local_type = @localType, local_article_id = @localArticleId, entity_id = @entityId,
                      actor_type = @actorType, actor_name = @actorName, via_agent_name = NULL
               WHERE sequence_num = @sequenceNum AND apply_state = {EventApplyStates.Held}",
            new { sequenceNum, localType, localArticleId, entityId, actorType, actorName });
    }

    public async Task SetHoldReasonAsync(long sequenceNum, string reason)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            $"UPDATE tbl_event SET hold_reason = @reason WHERE sequence_num = @sequenceNum AND apply_state = {EventApplyStates.Held}",
            new { sequenceNum, reason });
    }

    public async Task SetApplyStateAsync(long sequenceNum, int applyState)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            $@"UPDATE tbl_event SET apply_state = @applyState,
                      hold_reason = CASE WHEN @applyState = {EventApplyStates.Held} THEN hold_reason END,
                      held_at = CASE WHEN @applyState = {EventApplyStates.Held} THEN held_at END
               WHERE sequence_num = @sequenceNum",
            new { sequenceNum, applyState });
    }

    private sealed class HeldRowDto
    {
        public long SequenceNum { get; set; }
        public Guid EventId { get; set; }
        public Guid NodeId { get; set; }
        public long LamportTs { get; set; }
        public string EventType { get; set; } = "";
        public Guid? ArticleId { get; set; }
        public string? EntityId { get; set; }
        public string Payload { get; set; } = "";
        public byte[] Signature { get; set; } = [];
        public int ProtocolVersion { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? ActorType { get; set; }
        public string? ActorName { get; set; }
        public string? ViaAgentName { get; set; }
        public string? HoldReason { get; set; }
        public long? OriginSuperadmin { get; set; }
        public string? HeldAt { get; set; }
        public string? OriginPeer { get; set; }
        public long? OriginSeq { get; set; }
    }
}

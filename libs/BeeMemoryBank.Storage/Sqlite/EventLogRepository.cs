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

    public async Task AppendAsync(SyncEvent evt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_event
              (event_id, node_id, lamport_ts, event_type, article_id, entity_id, payload, signature, protocol_version, created_at, actor_type, actor_name, via_agent_name)
              VALUES (@EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @EntityId, @Payload, @Signature, @ProtocolVersion, @CreatedAt, @ActorType, @ActorName, @ViaAgentName)",
            evt);
    }

    public async Task<bool> AppendIfNotExistsAsync(SyncEvent evt)
    {
        using var conn = OpenConnection();
        var rows = await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_event
              (event_id, node_id, lamport_ts, event_type, article_id, entity_id, payload, signature, protocol_version, created_at, actor_type, actor_name, via_agent_name)
              VALUES (@EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @EntityId, @Payload, @Signature, @ProtocolVersion, @CreatedAt, @ActorType, @ActorName, @ViaAgentName)",
            evt);
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
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(lamport_ts), 0) FROM tbl_event");
    }

    public async Task<List<SyncEvent>> GetAfterSequenceAsync(long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event WHERE sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { afterSequenceNum, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0, string? eventType = null)
    {
        using var conn = OpenConnection();
        // Filter by event_type in SQL so LIMIT counts only matching rows.
        // No dedicated index on event_type — fine at current log sizes; add one if the log grows large.
        var where = string.IsNullOrEmpty(eventType) ? "" : "WHERE event_type = @eventType ";
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event {where}ORDER BY sequence_num DESC LIMIT @limit OFFSET @offset",
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
            $"SELECT {SelectCols} FROM tbl_event WHERE article_id = @articleId ORDER BY sequence_num DESC LIMIT @limit",
            new { articleId, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetAllAfterSequenceAsync(long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event WHERE sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { afterSequenceNum, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetLocalEventsAfterSequenceAsync(Guid nodeId, long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event WHERE node_id = @nodeId AND sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { nodeId, afterSequenceNum, limit })).ToList();
    }

    public async Task<List<SyncEvent>> GetEventsToRelayAsync(Guid excludeNodeId, long afterSequenceNum, int limit = 1000)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event WHERE node_id != @excludeNodeId AND sequence_num > @afterSequenceNum ORDER BY sequence_num LIMIT @limit",
            new { excludeNodeId, afterSequenceNum, limit })).ToList();
    }

    public async Task<bool> IsHardDeletedAsync(string entityId, long lamportTs)
    {
        // Query tbl_hard_delete_audit (NOT tbl_event) so the gate survives compaction —
        // tbl_event hard_delete rows get purged by CompactionService, but the audit
        // table is never compacted. Wave 2 audit kilo-1 #1 (CRIT).
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

    public async Task<SyncEvent?> GetByIdAsync(string eventId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event WHERE event_id = @eventId COLLATE NOCASE",
            new { eventId });
    }
}

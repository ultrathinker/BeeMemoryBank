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
        payload         AS Payload,
        signature       AS Signature,
        protocol_version AS ProtocolVersion,
        created_at      AS CreatedAt,
        actor_type      AS ActorType,
        actor_name      AS ActorName";

    public async Task AppendAsync(SyncEvent evt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_event
              (event_id, node_id, lamport_ts, event_type, article_id, payload, signature, protocol_version, created_at, actor_type, actor_name)
              VALUES (@EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @Payload, @Signature, @ProtocolVersion, @CreatedAt, @ActorType, @ActorName)",
            evt);
    }

    public async Task<bool> AppendIfNotExistsAsync(SyncEvent evt)
    {
        using var conn = OpenConnection();
        var rows = await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_event
              (event_id, node_id, lamport_ts, event_type, article_id, payload, signature, protocol_version, created_at, actor_type, actor_name)
              VALUES (@EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @Payload, @Signature, @ProtocolVersion, @CreatedAt, @ActorType, @ActorName)",
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

    public async Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncEvent>(
            $"SELECT {SelectCols} FROM tbl_event ORDER BY sequence_num DESC LIMIT @limit OFFSET @offset",
            new { limit, offset })).ToList();
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
}

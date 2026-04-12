using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class SyncPushPositionRepository(DbConnectionFactory factory) : BaseRepository(factory), ISyncPushPositionRepository
{
    public async Task<SyncPushPosition?> GetAsync(Guid remoteNodeId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<SyncPushPosition>(
            "SELECT remote_node_id AS RemoteNodeId, last_pushed_seq AS LastPushedSeq, pushed_at AS PushedAt FROM tbl_sync_push_position WHERE remote_node_id = @remoteNodeId",
            new { remoteNodeId });
    }

    public async Task UpsertAsync(SyncPushPosition position)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_sync_push_position (remote_node_id, last_pushed_seq, pushed_at)
              VALUES (@RemoteNodeId, @LastPushedSeq, @PushedAt)
              ON CONFLICT(remote_node_id) DO UPDATE SET
                last_pushed_seq = excluded.last_pushed_seq,
                pushed_at = excluded.pushed_at",
            position);
    }

    public async Task<List<SyncPushPosition>> GetAllAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncPushPosition>(
            "SELECT remote_node_id AS RemoteNodeId, last_pushed_seq AS LastPushedSeq, pushed_at AS PushedAt FROM tbl_sync_push_position")).ToList();
    }

    public async Task UpdatePositionAsync(Guid remoteNodeId, long lastPushedSeq)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_sync_push_position (remote_node_id, last_pushed_seq, pushed_at)
              VALUES (@remoteNodeId, @lastPushedSeq, @now)
              ON CONFLICT(remote_node_id) DO UPDATE SET 
                last_pushed_seq = MAX(last_pushed_seq, excluded.last_pushed_seq),
                pushed_at = excluded.pushed_at",
            new { remoteNodeId, lastPushedSeq, now = DateTime.UtcNow });
    }
}

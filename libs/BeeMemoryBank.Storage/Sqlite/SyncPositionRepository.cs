using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class SyncPositionRepository(DbConnectionFactory factory) : BaseRepository(factory), ISyncPositionRepository
{
    public async Task<SyncPosition?> GetAsync(Guid remoteNodeId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<SyncPosition>(
            "SELECT remote_node_id AS RemoteNodeId, last_sequence_num AS LastSequenceNum, updated_at AS UpdatedAt FROM tbl_sync_position WHERE remote_node_id = @remoteNodeId",
            new { remoteNodeId });
    }

    public async Task UpsertAsync(SyncPosition position)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_sync_position (remote_node_id, last_sequence_num, updated_at)
              VALUES (@RemoteNodeId, @LastSequenceNum, @UpdatedAt)
              ON CONFLICT(remote_node_id) DO UPDATE SET
                last_sequence_num = excluded.last_sequence_num,
                updated_at = excluded.updated_at",
            position);
    }

    public async Task<List<SyncPosition>> GetAllAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<SyncPosition>(
            "SELECT remote_node_id AS RemoteNodeId, last_sequence_num AS LastSequenceNum, updated_at AS UpdatedAt FROM tbl_sync_position")).ToList();
    }

    public async Task<List<(Guid NodeId, long LastSequenceNum, DateTime UpdatedAt)>> GetAllActivePositionsAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<(Guid NodeId, long LastSequenceNum, DateTime UpdatedAt)>(
            @"SELECT sp.remote_node_id, sp.last_sequence_num, sp.updated_at
              FROM tbl_sync_position sp
              JOIN tbl_whitelist w ON w.node_id = sp.remote_node_id
              WHERE w.status = 'A'");
        return rows.ToList();
    }
}

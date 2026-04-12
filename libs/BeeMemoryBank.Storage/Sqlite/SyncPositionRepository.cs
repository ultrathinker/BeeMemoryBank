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
}

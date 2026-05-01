using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class RestoreReplayShieldRepository(DbConnectionFactory factory) : BaseRepository(factory), IRestoreReplayShieldRepository
{
    public async Task<long?> GetShieldThresholdAsync(string peerNodeId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT ignore_events_before_lamport_ts FROM tbl_restore_replay_shield WHERE peer_node_id = @peerNodeId",
            new { peerNodeId });
    }

    public async Task UpsertShieldAsync(string peerNodeId, long ignoreEventsBeforeLamportTs, string shieldEventId, string createdAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_restore_replay_shield (peer_node_id, ignore_events_before_lamport_ts, shield_event_id, created_at)
              VALUES (@peerNodeId, @ignoreEventsBeforeLamportTs, @shieldEventId, @createdAt)
              ON CONFLICT(peer_node_id) DO UPDATE SET
                  ignore_events_before_lamport_ts = excluded.ignore_events_before_lamport_ts,
                  shield_event_id = excluded.shield_event_id,
                  created_at = excluded.created_at;",
            new { peerNodeId, ignoreEventsBeforeLamportTs, shieldEventId, createdAt });
    }

    public async Task DeleteShieldAsync(string peerNodeId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_restore_replay_shield WHERE peer_node_id = @peerNodeId",
            new { peerNodeId });
    }

    public async Task<List<(string PeerNodeId, long IgnoreEventsBeforeLamportTs, string ShieldEventId)>> GetAllAsync()
    {
        using var conn = OpenConnection();
        var results = await conn.QueryAsync<(string PeerNodeId, long IgnoreEventsBeforeLamportTs, string ShieldEventId)>(
            @"SELECT 
                peer_node_id AS PeerNodeId, 
                ignore_events_before_lamport_ts AS IgnoreEventsBeforeLamportTs, 
                shield_event_id AS ShieldEventId 
              FROM tbl_restore_replay_shield");
        return results.ToList();
    }
}

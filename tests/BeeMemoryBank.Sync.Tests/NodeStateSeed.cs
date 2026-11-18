using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>A peer's <c>node_state</c> report as a node stores it, for fixtures that wire peers by hand.</summary>
internal static class NodeStateSeed
{
    /// <summary>
    /// <paramref name="nodeId"/> reported <paramref name="ownSuperadminDoors"/> superadmin doors of its own (<c>local_superadmin_door_count</c>): a node
    /// with an account of its own may be named accounts node by a take-over or a first designation; one with 0 holds mirrors only.
    /// </summary>
    public static async Task ReportsOwnSuperadminsAsync(IDbConnectionFactory factory, Guid nodeId, int ownSuperadminDoors = 1)
    {
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR REPLACE INTO tbl_node_state (node_id, app_version, platform, runtime_kind, identity_key_fp, dek_fp, attention_flags_json, extra_json,
                  lamport_ts, event_id, updated_at)
              VALUES (@Node, '2.12.0', 'test', 'server', '', NULL, '[]', @Extra, 0, NULL, @Now)",
            new
            {
                Node = nodeId.ToString("D"),
                Extra = $"{{\"local_superadmin_door_count\":\"{ownSuperadminDoors}\"}}",
                Now = DateTime.UtcNow.ToString("O"),
            });
    }
}

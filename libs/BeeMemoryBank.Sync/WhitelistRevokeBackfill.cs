using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Idempotent one-time backfill that heals databases affected by a bug in earlier
/// versions of JoinEndpoints. That code used to silently set tbl_whitelist.status='R'
/// on stale rows (same DisplayName, new NodeId) without emitting a whitelist_revoke
/// event. The result: the revocation never propagated via sync, and peers that
/// replayed history from scratch would see the ghost nodes re-activated.
///
/// This bootstrapper runs on every startup. It finds every tbl_whitelist row that is
/// revoked but has no corresponding whitelist_revoke event, and emits one via the
/// EventLogger (properly signed by the current node's Ed25519 key with a fresh
/// lamport timestamp). Once run, subsequent invocations are no-ops.
///
/// Semantically correct: the local node is the authority for its own whitelist, so
/// "this node revokes this ghost right now" is a valid assertion.
/// </summary>
public class WhitelistRevokeBackfill(
    DbConnectionFactory dbFactory,
    INodeIdentityRepository nodeRepo,
    IEventLogger eventLogger)
{
    public async Task<int> RunIfNeededAsync()
    {
        var identity = await nodeRepo.GetAsync();
        if (identity == null) return 0;

        // The self-node must never be revoked via event. Any stale tbl_whitelist row
        // with status='R' for the current node's own NodeId is a historical artifact
        // (e.g. a very old Join where the server wrongly added itself). Emitting a
        // revoke event for the self-node would be catastrophic: it would tell every
        // peer to stop trusting events from us, breaking sync network-wide.
        var selfId = identity.NodeId.ToString().ToUpperInvariant();

        List<string> orphanNodeIds;
        using (var conn = dbFactory.CreateConnection())
        {
            // Revoked whitelist rows that lack a matching whitelist_revoke event.
            // node_id in tbl_whitelist is uppercase; node_id inside payload JSON is lowercase.
            orphanNodeIds = (await conn.QueryAsync<string>(
                @"SELECT w.node_id
                  FROM tbl_whitelist w
                  WHERE w.status = 'R'
                    AND upper(w.node_id) != @SelfId
                    AND NOT EXISTS (
                        SELECT 1 FROM tbl_event e
                        WHERE e.event_type = 'whitelist_revoke'
                          AND json_extract(e.payload, '$.node_id') = lower(w.node_id)
                    )",
                new { SelfId = selfId })).ToList();
        }

        if (orphanNodeIds.Count == 0) return 0;

        foreach (var nodeIdStr in orphanNodeIds)
        {
            if (!Guid.TryParse(nodeIdStr, out var targetNodeId))
                continue;
            await eventLogger.LogWhitelistRevokeAsync(targetNodeId);
        }

        return orphanNodeIds.Count;
    }
}

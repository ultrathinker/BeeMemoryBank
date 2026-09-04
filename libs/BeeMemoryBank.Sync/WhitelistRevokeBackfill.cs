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
/// It finds every tbl_whitelist row that is revoked but has no corresponding whitelist_revoke
/// event, and emits one via the EventLogger (properly signed by the current node's Ed25519 key with
/// a fresh lamport timestamp). Once run, subsequent invocations are no-ops.
///
/// <para>
/// <b>NOTHING CALLS THIS.</b> The line above used to say it "runs on every startup"; it does not,
/// and has not for as long as the current tree goes back — no type constructs it and
/// <c>ApiStartupTasks</c> runs a different bootstrapper. Said plainly here because a comment
/// claiming a heal happens automatically is worse than no comment: it stops the next reader from
/// checking.
/// </para>
///
/// <para>
/// Wiring it up is a real decision, not an oversight to quietly correct, and it got MORE
/// consequential with migration 021. Whitelist rows now carry a version, and legacy revoked rows
/// sit at version 0 — which loses to any incoming <c>whitelist_add</c>, so exactly the ghost-node
/// resurrection this class was written to prevent is what a stale add now produces against those
/// rows. Running the backfill fixes them (it stamps the version alongside the event). But it also
/// emits real revoke events to every peer, and that is an outward-facing action on live data that
/// belongs in a session where somebody is watching, not in a startup path that fires unattended.
/// </para>
///
/// Semantically correct: the local node is the authority for its own whitelist, so
/// "this node revokes this ghost right now" is a valid assertion.
/// </summary>
public class WhitelistRevokeBackfill(
    DbConnectionFactory dbFactory,
    INodeIdentityRepository nodeRepo,
    IEventLogger eventLogger,
    IWhitelistRepository whitelistRepo)
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
            // Stamp the row with the version of the event we just published. These rows were
            // revoked before this node logged revoke events at all, so they sit at version 0 —
            // which loses to every add, including one issued before the revocation. Announcing the
            // revoke without also versioning the row would leave exactly the hole this backfill
            // exists to close, just moved one step later.
            var version = await eventLogger.LogWhitelistRevokeAsync(targetNodeId);
            await whitelistRepo.SetVersionAsync(targetNodeId, version);
        }

        return orphanNodeIds.Count;
    }
}

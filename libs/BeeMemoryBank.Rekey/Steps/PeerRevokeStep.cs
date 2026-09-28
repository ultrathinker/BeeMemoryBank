using Dapper;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// Every other node is revoked in the copy (rekey-offline.md §2 step 3, §8.2; L): full nodes, phones and blind nodes
/// alike, as the v10.2 fence did. Offline, and with the event log about to be cleared, no whitelist_revoke is signed
/// or sent: the rows are revoked here, so the re-keyed node refuses every old peer's sync. Devices join again as new
/// profiles, and each blind node is paired again with a fresh code (the report page says so).
/// <para>Each revoke takes a version above every other in the copy, so no whitelist event that predates the re-key
/// can bring a peer back. Notes list the peers for the report: <c>revoked:&lt;node id&gt; &lt;name&gt;</c>.</para>
/// </summary>
public sealed class PeerRevokeStep : IRekeyStep
{
    public string Name => "PeerRevoke";

    public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
    {
        var db = ctx.Main;
        var notes = new List<string>();
        using var tx = db.BeginTransaction();

        var peers = (await db.QueryAsync<(string NodeId, string Name)>(
            "SELECT node_id, display_name FROM tbl_whitelist WHERE status <> 'R' AND node_id <> @self COLLATE NOCASE ORDER BY node_id",
            new { self = ctx.NodeId.ToString() }, tx)).ToList();
        var lamport = await RekeyLamport.MaxAsync(db, tx) + 1;
        var now = DateTime.UtcNow;
        foreach (var p in peers)
        {
            await db.ExecuteAsync(
                @"UPDATE tbl_whitelist
                  SET status = 'R', deleted_at = @now, updated_at = @now, lamport_ts = @lamport, source_node_id = @self
                  WHERE node_id = @node",
                new { now, lamport, self = ctx.NodeId.ToString(), node = p.NodeId }, tx);
            notes.Add($"revoked:{(Guid.TryParse(p.NodeId, out var g) ? g.ToString() : p.NodeId)} {p.Name}");
        }

        tx.Commit();
        ctx.Progress.Report(Name, 1, 1);
        return new RekeyStepResult(Name, new Dictionary<string, long> { ["tbl_whitelist"] = peers.Count }, notes);
    }

    public async Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var active = (await ctx.Main.QueryAsync<string>(
            "SELECT node_id FROM tbl_whitelist WHERE status <> 'R' AND node_id <> @self COLLATE NOCASE",
            new { self = ctx.NodeId.ToString() })).ToList();
        return active.Select(n => new RekeyProblem("tbl_whitelist", n, "a peer is still trusted")).ToList();
    }
}

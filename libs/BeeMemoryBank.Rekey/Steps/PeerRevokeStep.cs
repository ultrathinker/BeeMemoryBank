using Dapper;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// Every other node is revoked in the copy (rekey-offline.md §2 step 3, §8.2; L): full nodes, phones and blind nodes
/// alike, as the v10.2 fence did. Offline, and with the event log about to be cleared, no whitelist_revoke is signed
/// or sent: the rows are revoked here, so the re-keyed node refuses every old peer's sync. Devices join again as new
/// profiles, and each blind node is paired again with a fresh code (the report page says so).
/// <para>Each revoke takes a version above every other in the copy, so no whitelist event that predates the re-key
/// can bring a peer back. Notes list the peers for the report: <c>revoked:&lt;node id&gt; &lt;name&gt;</c>.</para>
/// <para>The invitations this node made to blind copies (<c>tbl_blind_invite</c>, release 2.7) go with the peers they would have added: they are about who may
/// join, and a redeemed one holds the device's backup key under the old master key, which no longer opens.</para>
/// <para>What the peers announced about themselves and this node's ways to reach them (<c>tbl_peer_endpoint</c>, <c>tbl_peer_channel</c>, migration 044) go
/// with the peers too.</para>
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
        // The revoke's version is the row's membership version too (migration 045): an add is judged against that one. A database from before
        // 045 has no such column; the migration copies lamport_ts into it when it runs.
        var memberColumn = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('tbl_whitelist') WHERE name = 'member_lamport_ts'", transaction: tx) > 0;
        var member = memberColumn ? ", member_lamport_ts = @lamport, member_source_node_id = @self" : "";
        foreach (var p in peers)
        {
            await db.ExecuteAsync(
                $@"UPDATE tbl_whitelist
                  SET status = 'R', deleted_at = @now, updated_at = @now, lamport_ts = @lamport, source_node_id = @self{member}
                  WHERE node_id = @node",
                new { now, lamport, self = ctx.NodeId.ToString(), node = p.NodeId }, tx);
            notes.Add($"revoked:{(Guid.TryParse(p.NodeId, out var g) ? g.ToString() : p.NodeId)} {p.Name}");
        }

        var invitations = await db.ExecuteAsync("DELETE FROM tbl_blind_invite", transaction: tx);
        var counts = new Dictionary<string, long> { ["tbl_whitelist"] = peers.Count, ["tbl_blind_invite"] = invitations };
        foreach (var table in PeerTables)
            if (await TableExistsAsync(db, table, tx))
                counts[table] = await db.ExecuteAsync($"DELETE FROM {table}", transaction: tx);

        tx.Commit();
        ctx.Progress.Report(Name, 1, 1);
        return new RekeyStepResult(Name, counts, notes);
    }

    public async Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var active = (await ctx.Main.QueryAsync<string>(
            "SELECT node_id FROM tbl_whitelist WHERE status <> 'R' AND node_id <> @self COLLATE NOCASE",
            new { self = ctx.NodeId.ToString() })).ToList();
        var problems = active.Select(n => new RekeyProblem("tbl_whitelist", n, "a peer is still trusted")).ToList();
        var left = await ctx.Main.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blind_invite");
        if (left > 0) problems.Add(new RekeyProblem("tbl_blind_invite", "(rows)", "an invitation to a blind copy is still there"));
        foreach (var table in PeerTables)
            if (await TableExistsAsync(ctx.Main, table, null) && await ctx.Main.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}") > 0)
                problems.Add(new RekeyProblem(table, "(rows)", "what a revoked peer announced, or a way to reach it, is still there"));
        return problems;
    }

    private static readonly string[] PeerTables = ["tbl_peer_endpoint", "tbl_peer_channel"];

    private static async Task<bool> TableExistsAsync(System.Data.IDbConnection db, string table, System.Data.IDbTransaction? tx) =>
        await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }, tx) > 0;
}

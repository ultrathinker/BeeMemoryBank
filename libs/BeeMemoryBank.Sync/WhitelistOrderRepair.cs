using BeeMemoryBank.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Puts right, once, the whitelist rows an older build left wrong because their events arrived in an unlucky order. Such a build dropped a
/// <c>whitelist_revoke</c> that found no row (the revoke came before the add) yet logged it as applied, so the add that came next made an active
/// row and the revoke was never seen again: the node trusted a peer the rest of the network had revoked, and handed it on to every node that
/// joined through it. It dropped an update that came before its add the same way, and an add over an active row left the row's version where it
/// was, so an older revoke arriving later still won (a peer revoked and added again elsewhere stayed revoked here). And it kept one version per
/// row: a revoke that an update crossed (one made by a node that had not seen the revoke yet) lost to that update's version, and so did a
/// demotion that a rename crossed, on every node that got the update first.
///
/// <para>Migration 045 gives every concern of a row (membership, superadmin, name, address, TLS trust) the row's version, the fields undated
/// (<see cref="Core.Models.WhitelistEntry.UndatedFields"/>: an add that is not a re-add does not change them, as an older build never let an add
/// change an active row). The repair then dates a concern of an active row where the log shows which event wrote it, and takes every whitelist event the
/// log holds through the rules the applier uses now (<see cref="EventApplier.RepairWhitelistOrderAsync"/>), in version order, and logs every
/// row it changed (a warning when who this node trusts changed). It needs no key (the whitelist and its events are clear on every node) and runs
/// before anything is served or synced: from <see cref="StoredEventRepair.RunAsync"/>, which every host runs at its start. A marker in
/// <c>tbl_migration_marker</c> makes it one-off; it is only written after a run that finished, so a failed or interrupted run is tried again at the next
/// start (a second run is harmless: it changes nothing the first one left). Its key is new with migration 045, so a node that ran the repair of the build
/// before (without versions per concern) runs this one too.</para>
///
/// <para>What it cannot know it leaves alone, and never adds a peer or raises one to superadmin on it. A row with no whitelist event in the log
/// (joined from a host, restored, or older than a compaction) is not touched; a concern keeps the row's version unless the event that gave the row
/// that version is in the log and wrote something else, the row is younger than the last compaction, join and restore (so the log holds its whole
/// history), and the revoke or demotion that would then apply was dropped by an older build for a newer event the log holds; a row whose
/// revoke or demotion is held back that way is logged as a warning. A peer with no row gets at most a revoked one, when the newest add or revoke
/// the log holds for it is a revoke; and a peer whose revoke never reached this node stays as it is: revoke it under Admin &gt; Devices, on the
/// device page.</para>
/// </summary>
public static class WhitelistOrderRepair
{
    // whitelist_order_repaired was the key of the repair before versions per concern (never released); this one runs once on every node.
    public const string MarkerKey = "whitelist_concern_versions_repaired";

    /// <returns>The number of rows it changed; 0 when it ran before, when the node is not initialized yet, or when the host has no applier.</returns>
    public static async Task<int> RunAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using (var conn = db.CreateConnection())
        {
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @MarkerKey", new { MarkerKey }) > 0)
                return 0;
        }
        // A node that is not set up yet, or whose log holds no whitelist event, has nothing to repair and writes nothing (not even the marker: a
        // start writes nothing it does not have to). It is asked again at its next start; by the time its log has such events, every one of them
        // was applied by this build, and the run that then writes the marker changes nothing.
        if (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync() is null) return 0;
        if ((await WhitelistHistory.PeersAsync(db)).Count == 0) return 0;
        if (scope.ServiceProvider.GetService<EventApplier>() is not { } applier) return 0;

        var changes = await applier.RepairWhitelistOrderAsync(ct);
        foreach (var change in changes)
        {
            // A peer that became revoked or active again is what the repair is for, and what an admin has to know. A row whose version or fields
            // moved (a joined row taking the version of an add the log holds, an update that came before its add) is a line of information.
            if (change.Membership)
                logger.LogWarning("Whitelist repair: the events in the log change who this node trusts. {NodeId}: {Before} -> {After}",
                    change.NodeId, change.Before, change.After);
            else
                logger.LogInformation("Whitelist repair: {NodeId}: {Before} -> {After}", change.NodeId, change.Before, change.After);
        }

        using (var conn = db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES (@MarkerKey, @Value, @Now)",
                new { MarkerKey, Value = changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Now = DateTime.UtcNow.ToString("O") });
        }
        logger.LogInformation("Whitelist repair done: {Count} row(s) changed", changes.Count);
        return changes.Count;
    }
}

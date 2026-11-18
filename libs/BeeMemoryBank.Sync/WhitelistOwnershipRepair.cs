using BeeMemoryBank.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// One-off late-upgrade repair after migration 047: replays optional ownership fields from stored
/// <c>whitelist_add</c> / <c>whitelist_update</c> events in <c>tbl_event</c> onto <c>tbl_whitelist</c>,
/// so a node that ran an older build while peers already published ownership catches up from its own log.
/// </summary>
public static class WhitelistOwnershipRepair
{
    public const string MarkerKey = "whitelist_ownership_repaired";

    public static async Task<int> RunAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using (var conn = db.CreateConnection())
        {
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @MarkerKey", new { MarkerKey }) > 0)
                return 0;
        }
        if (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync() is null) return 0;
        if ((await WhitelistHistory.PeersAsync(db)).Count == 0) return 0;
        if (scope.ServiceProvider.GetService<EventApplier>() is not { } applier) return 0;

        var changed = await applier.RepairWhitelistOwnershipAsync(ct);

        using (var conn = db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES (@MarkerKey, @Value, @Now)",
                new { MarkerKey, Value = changed.ToString(System.Globalization.CultureInfo.InvariantCulture), Now = DateTime.UtcNow.ToString("O") });
        }
        logger.LogInformation("Whitelist ownership repair done: {Count} row(s) changed", changed);
        return changed;
    }
}

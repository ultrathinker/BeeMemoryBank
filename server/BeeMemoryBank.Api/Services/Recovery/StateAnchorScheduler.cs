using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync.Recovery;
using Dapper;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// Publishes integrity anchors (plan 5.5) from a full node that is open, has a blind node to anchor
/// for, is a superadmin by the network's signed standing, and has caught up with the mesh — an anchor from a node that is behind would vouch for a
/// state the others have already moved past. A new anchor only when the state changed since this
/// node's last one, and not more often than <see cref="MinInterval"/>: the blind node keeps signed
/// events after the newest anchor, so anchoring regularly keeps that tail — and a restore's list of
/// rows checked only by signature — short.
/// </summary>
public class StateAnchorScheduler(
    IServiceScopeFactory scopes,
    SessionService session,
    ILogger<StateAnchorScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);
    // A peer we pull from counts as caught up when its position moved this recently.
    internal static readonly TimeSpan FreshPull = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PublishIfDueAsync(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "State anchor tick failed");
            }
        }
    }

    /// <returns>True when an anchor was published.</returns>
    public async Task<bool> PublishIfDueAsync(DateTime nowUtc)
    {
        if (!session.IsUnlocked) return false;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;

        var peers = await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        if (!peers.Any(p => BlindNodeId.IsBlind(p.NodeId))) return false;

        // Anchors are superadmin-only: without the network's signed word that this node is one, it
        // publishes nothing (a restore would not trust such an anchor, and peers would refuse it).
        if (!await sp.GetRequiredService<IOwnStandingProvider>().IsSuperadminAsync()) return false;

        // Caught up: every peer this node pulls from (it has an address) was pulled from recently,
        // and nothing is stuck in quarantine.
        var positions = (await sp.GetRequiredService<ISyncPositionRepository>().GetAllActivePositionsAsync())
            .ToDictionary(p => p.NodeId, p => p.UpdatedAt);
        foreach (var peer in peers.Where(p => !string.IsNullOrWhiteSpace(p.ApiAddress)))
        {
            if (!positions.TryGetValue(peer.NodeId, out var updated) || nowUtc - updated.ToUniversalTime() > FreshPull)
                return false;
        }
        var connFactory = sp.GetRequiredService<IDbConnectionFactory>();
        using (var conn = connFactory.CreateConnection())
        {
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_sync_quarantine") > 0)
                return false;
        }

        var me = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
        if (me == null) return false;

        (string Digest, string CreatedAt)? last;
        StateDigestResult state;
        using (var conn = connFactory.CreateConnection())
        {
            last = await conn.QueryFirstOrDefaultAsync<(string, string)?>(
                @"SELECT digest, created_at FROM tbl_state_anchor WHERE author_node_id = @Me COLLATE NOCASE
                  ORDER BY created_at DESC LIMIT 1", new { Me = me.Value.ToString() });
            state = await StateDigest.ComputeAsync(conn, includeSelfAsSuperadmin: true); // as PublishAsync computes it
        }
        if (last is { } l)
        {
            if (l.Digest == state.Digest) return false;
            if (DateTime.TryParse(l.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && nowUtc - at.ToUniversalTime() < MinInterval)
                return false;
        }

        await sp.GetRequiredService<StateAnchorService>().PublishAsync();
        logger.LogInformation("Published an integrity anchor");
        return true;
    }
}

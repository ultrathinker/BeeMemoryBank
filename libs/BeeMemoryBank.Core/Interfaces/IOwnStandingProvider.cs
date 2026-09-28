namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Whether THIS node is a superadmin in the network's eyes (plan 4.2, 5.5). A node's own row lives only in
/// its peers' whitelists, never in its own, so it cannot know from local data; the answer comes from the
/// peers' standing (<c>GET /api/sync/my-standing</c>, authenticated and pinned; PeerOwnStanding on a full
/// Api node). Superadmin-only work this node starts itself — integrity anchors, retiring other nodes'
/// recovery boxes — asks here first.
/// </summary>
public interface IOwnStandingProvider
{
    Task<bool> IsSuperadminAsync(CancellationToken ct = default);
}

/// <summary>
/// Where no peer can be asked (a host without the Api's peer client), the answer is "not known to be a
/// superadmin": superadmin-only events are not published at all rather than published and refused (and
/// quarantined) by every peer.
/// </summary>
public sealed class UnknownOwnStanding : IOwnStandingProvider
{
    public Task<bool> IsSuperadminAsync(CancellationToken ct = default) => Task.FromResult(false);
}

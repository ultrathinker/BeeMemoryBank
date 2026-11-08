using System.Net;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Sync.Channels;

/// <summary>One way to reach a peer, as the planner judged it.</summary>
/// <param name="Url">The canonical address.</param>
/// <param name="Source">Where it came from (<see cref="ChannelSources"/>; the most explicit of them when several named it).</param>
/// <param name="Scope"><see cref="ChannelScopes"/>.</param>
/// <param name="OnCurrentNetwork">For a LAN or VPN address: this computer is on its network now. Always true for an internet address.</param>
/// <param name="LastOkAt">When a session through it last went through (any source's row of the address).</param>
/// <param name="LastFailAt">When it last failed.</param>
/// <param name="LastFailKind">Why (<see cref="ChannelFailures"/>).</param>
public sealed record ChannelCandidate(string Url, string Source, string Scope, bool OnCurrentNetwork, DateTime? LastOkAt, DateTime? LastFailAt, string? LastFailKind)
{
    public bool IsLanLike => Scope is ChannelScopes.Lan or ChannelScopes.Vpn;
}

/// <summary>
/// The order in which a session tries a peer's addresses (docs/design/lan-peer-sync.md section 7): the one that worked last; then the LAN and VPN
/// addresses of networks this computer is on, all at once with a short timeout; then the internet ones, one after another; then a LAN address of a
/// network this computer is NOT on that a person gave explicitly (the primary address, a typed one), with the short timeout. An announced or mDNS
/// address of a network this computer is not on is not dialled at all: no timeouts while travelling.
/// </summary>
public sealed record ChannelPlan(
    IReadOnlyList<ChannelCandidate> First,
    IReadOnlyList<ChannelCandidate> Parallel,
    IReadOnlyList<ChannelCandidate> Sequential,
    IReadOnlyList<ChannelCandidate> Skipped)
{
    public static readonly ChannelPlan Empty = new([], [], [], []);

    /// <summary>Every address that is dialled, in order.</summary>
    public IReadOnlyList<ChannelCandidate> Dialable => [.. First, .. Parallel, .. Sequential];

    /// <summary>Every address the peer has, dialled or not.</summary>
    public IReadOnlyList<ChannelCandidate> All => [.. Dialable, .. Skipped];

    public bool HasDialable => First.Count + Parallel.Count + Sequential.Count > 0;

    /// <summary>The peer has addresses, and every one of them is a LAN or VPN address.</summary>
    public bool OnlyLan => All.Count > 0 && All.All(c => c.IsLanLike);

    /// <summary>One of the peer's LAN or VPN addresses is on a network this computer is on now.</summary>
    public bool AnyLanOnCurrentNetwork => All.Any(c => c.IsLanLike && c.OnCurrentNetwork);

    /// <summary>The set of addresses that are dialled, for "did a new way to reach the peer appear".</summary>
    public string Fingerprint => string.Join('|', Dialable.Select(c => c.Url).Order(StringComparer.Ordinal));

    /// <summary>
    /// The plan with only the addresses a person gave (the primary one, a typed one): for a session whose client cannot carry the peer's TLS anchor,
    /// where an announced or mDNS address would be checked by its host alone.
    /// </summary>
    public ChannelPlan WithoutAutomatic()
    {
        static bool Given(ChannelCandidate c) => c.Source is ChannelSources.Primary or ChannelSources.Manual;
        return new ChannelPlan(First.Where(Given).ToList(), Parallel.Where(Given).ToList(), Sequential.Where(Given).ToList(),
            [.. Skipped, .. Dialable.Where(c => !Given(c))]);
    }
}

/// <summary>Builds a <see cref="ChannelPlan"/> from what this node knows about a peer. Pure: the stores are read by the caller.</summary>
public static class ChannelPlanner
{
    /// <summary>
    /// An announced or mDNS candidate that showed another peer (or a TLS key the anchor refused) is not dialled again for this long. A primary or typed
    /// address that did is still dialled, last: a person gave it, and a passing failure (a captive portal, a certificate being replaced) must not leave
    /// a peer with one address undialled for an hour.
    /// </summary>
    public static readonly TimeSpan WrongPeerPause = TimeSpan.FromHours(1);

    private static int Rank(string source) => source switch
    {
        ChannelSources.Manual => 0,
        ChannelSources.Primary => 1,
        ChannelSources.Announced => 2,
        _ => 3,
    };

    /// <param name="ignorePause">True for a person's "Check now": an address paused after a wrong answer is dialled too.</param>
    public static ChannelPlan Plan(
        WhitelistEntry row, PeerEndpointRecord? announced, IReadOnlyList<DiscoveredEndpoint> discovered, IReadOnlyList<PeerChannelRow> rows,
        IReadOnlyList<LocalSubnet> local, DateTime nowUtc, bool ignorePause = false)
    {
        // Every address, by source; the same address named twice is one candidate under its most explicit source.
        var named = new List<(string Url, string Source, string? Scope)>();
        if (PeerAddressPolicy.ForStorage(row.ApiAddress) is { } primary) named.Add((primary, ChannelSources.Primary, null));

        // An address nobody gave by hand - the peer's announcement, an mDNS record anyone on the network can send - is a candidate only where the
        // session can hold it to the peer's own TLS keys: the peer has a pinned leaf or CA key (its row's pin, or what it announced or gave at the
        // join), and the address is a LAN or VPN one, never a name on the internet (docs/design/lan-peer-sync.md 14).
        var holdable = PeerTlsAnchor.For(row, announced) is not null;
        if (holdable)
        {
            foreach (var e in announced?.Endpoints ?? [])
                if (e.Scope is ChannelScopes.Lan or ChannelScopes.Vpn && PeerAddressPolicy.ForStorage(e.Url) is { } url && IsAutomaticHost(url))
                    named.Add((url, ChannelSources.Announced, e.Scope));
            var freshCutoff = nowUtc - IPeerDiscovery.Freshness;
            foreach (var d in discovered)
                if (d.NodeId == row.NodeId && d.SeenAtUtc >= freshCutoff && PeerAddressPolicy.ForStorage(d.Url) is { } url && IsAutomaticHost(url))
                    named.Add((url, ChannelSources.Mdns, null));
        }
        foreach (var r in rows)
            if (r.Source == ChannelSources.Manual && PeerAddressPolicy.ForStorage(r.Address) is { } url) named.Add((url, ChannelSources.Manual, null));

        var candidates = new List<ChannelCandidate>();
        foreach (var group in named.GroupBy(n => n.Url, StringComparer.Ordinal))
        {
            var best = group.OrderBy(g => Rank(g.Source)).First();
            var scope = group.Select(g => g.Scope).FirstOrDefault(s => s is not null) ?? ChannelScopes.Of(group.Key);
            var stats = rows.Where(r => string.Equals(r.Address, group.Key, StringComparison.Ordinal)).ToList();
            var lastOk = stats.Max(s => s.LastOkAt);
            var lastFail = stats.OrderByDescending(s => s.LastFailAt).FirstOrDefault();
            candidates.Add(new ChannelCandidate(group.Key, best.Source, scope, IsOnCurrentNetwork(group.Key, scope, local), lastOk,
                lastFail?.LastFailAt, lastFail?.LastFailKind));
        }

        var dialable = new List<ChannelCandidate>();
        var skipped = new List<ChannelCandidate>();
        var givenButWrong = new List<ChannelCandidate>();
        foreach (var c in candidates)
        {
            var automatic = c.Source is ChannelSources.Announced or ChannelSources.Mdns;
            var wrongPeerRecently = !ignorePause && c.LastFailKind == ChannelFailures.WrongPeer && c.LastFailAt is { } failed
                && nowUtc - failed < WrongPeerPause && (c.LastOkAt is null || c.LastOkAt < failed);
            var offNetworkAutomatic = c.IsLanLike && !c.OnCurrentNetwork && automatic;
            if (offNetworkAutomatic || (wrongPeerRecently && automatic)) skipped.Add(c);
            else if (wrongPeerRecently) givenButWrong.Add(c);
            else dialable.Add(c);
        }

        // The one that worked last goes first - while its network is one this computer is on (a home address is not tried first in a café).
        var first = dialable.Where(c => c.LastOkAt is not null && c.OnCurrentNetwork).OrderByDescending(c => c.LastOkAt).Take(1).ToList();
        var rest = dialable.Except(first).ToList();
        var parallel = rest.Where(c => c.IsLanLike && c.OnCurrentNetwork).OrderBy(c => Rank(c.Source)).ThenByDescending(c => c.LastOkAt).ToList();
        var internet = rest.Where(c => !c.IsLanLike).OrderBy(c => Rank(c.Source)).ToList();
        var farExplicit = rest.Where(c => c.IsLanLike && !c.OnCurrentNetwork).OrderBy(c => Rank(c.Source)).ToList();
        return new ChannelPlan(first, parallel, [.. internet, .. farExplicit, .. givenButWrong.OrderBy(c => Rank(c.Source))], skipped);
    }

    /// <summary>An address an announcement or an mDNS record may name: a LAN or VPN one (a private-range, CGNAT or unique-local literal, a <c>.local</c> name).</summary>
    public static bool IsAutomaticHost(string url) => ChannelScopes.Of(url) is ChannelScopes.Lan or ChannelScopes.Vpn;

    /// <summary>For a LAN or VPN address: is this computer on its network now. A <c>.local</c> name and a loopback address always are; an internet address is reachable from anywhere.</summary>
    public static bool IsOnCurrentNetwork(string url, string scope, IReadOnlyList<LocalSubnet> local)
    {
        if (scope == ChannelScopes.Internet) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return local.Any(l => !l.Vpn);
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return true;
        if (IPAddress.IsLoopback(ip)) return true;
        if (scope == ChannelScopes.Vpn)
            return local.Any(l => l.Vpn) || local.Any(l => l.Contains(ip));
        return local.Any(l => !l.Vpn && l.Contains(ip));
    }
}

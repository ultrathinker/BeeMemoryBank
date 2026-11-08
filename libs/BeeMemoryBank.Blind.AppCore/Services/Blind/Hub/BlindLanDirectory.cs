using System.Collections.Concurrent;
using System.Net;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// One answer of an mDNS browse: a node of this product on the network, as its record says. Plain values on purpose: a host that browses (the desktop heads
/// with Makaretu, a phone with NSD or Bonjour) names no type of the sync library, and the rule that decides what is worth keeping is here, in one place.
/// </summary>
/// <param name="NodeId">The node id of the record's TXT <c>nodeId</c>; <see cref="Guid.Empty"/> when it has none.</param>
/// <param name="Host">The address the record resolved to (an IPv4 address when there is one), or its host name.</param>
/// <param name="Port">The port of its SRV record: the node's LAN listener.</param>
/// <param name="Https">The record says the node serves https.</param>
/// <param name="ServesSync">The record says the sync routes answer there (TXT <c>surface</c> holds <c>sync</c> or <c>web</c>, or is absent in a record of a build before LAN sync).</param>
public sealed record BlindLanSighting(Guid NodeId, string Host, int Port, bool Https, bool ServesSync);

/// <summary>
/// The host's mDNS browse (<c>_beememorybank._tcp</c>): one bounded scan that returns every node of the product it hears. The desktop heads implement it over
/// Makaretu; a head that cannot browse registers none, and a copy then reaches its hub by the pairing address and the endpoints the hub announced.
/// </summary>
public interface IBlindLanBrowser
{
    /// <summary>Listens for <paramref name="scanTime"/> and returns what it heard. An environment that cannot browse (no multicast) returns nothing; it does not throw.</summary>
    Task<IReadOnlyList<BlindLanSighting>> BrowseAsync(TimeSpan scanTime, CancellationToken ct);
}

/// <summary>
/// Where the hubs of this device were seen on the network (docs/design/lan-peer-sync.md section 7, source <c>mdns</c>), shared by every account of the device: ONE scan
/// serves them all. It is the copy's <see cref="IPeerDiscovery"/> - the channel planner reads <see cref="Discovery"/> for the addresses a hub was seen at in the last ten
/// minutes - and it scans only when a hub call asks (<see cref="LookAsync"/>): at most every <see cref="BrowseEvery"/> while a hub has no fresh sighting, and, when a hub
/// stops answering, at once (at most every <see cref="UrgentGap"/>). Nothing runs in the background, so a copy whose hub is elsewhere on the internet opens no multicast socket.
/// <para>Only the records of the hubs a call asked about are kept (<see cref="Watch"/>), only those that serve https, with the sync routes, at an IP address of a LAN or VPN
/// (the rule of <c>MdnsPeerBrowser.IsPeerSighting</c>, which the desktop node applies to its paired peers). A forged record only adds a candidate: it is dialled only
/// under the hub's own TLS key and identity.</para>
/// </summary>
public sealed class BlindLanDirectory(IBlindLanBrowser? browser = null, TimeProvider? time = null, ILogger<BlindLanDirectory>? logger = null)
{
    /// <summary>The least time between two scans while a hub has no fresh sighting.</summary>
    public static readonly TimeSpan BrowseEvery = TimeSpan.FromMinutes(5);

    /// <summary>The least time between two scans when a hub stopped answering (several accounts failing together scan once).</summary>
    public static readonly TimeSpan UrgentGap = TimeSpan.FromSeconds(30);

    /// <summary>How long a scan listens.</summary>
    public static TimeSpan ScanTime { get; set; } = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly PeerDiscoveryCache _cache = new();
    private readonly ConcurrentDictionary<Guid, byte> _hubs = new();
    private readonly SemaphoreSlim _scan = new(1, 1);
    private DateTimeOffset? _lastScanAt;

    /// <summary>The sightings, as the channel planner reads them.</summary>
    public IPeerDiscovery Discovery => _cache;

    /// <summary>This host can browse.</summary>
    public bool CanBrowse => browser is not null;

    /// <summary>The hub <paramref name="hub"/> is one of this device's: its records are kept from now on.</summary>
    public void Watch(Guid hub)
    {
        if (hub != Guid.Empty) _hubs[hub] = 0;
    }

    /// <summary>
    /// Looks for <paramref name="hub"/> on the network when that is due. Never throws for a scan that failed (the environment may have no multicast); only for cancellation.
    /// </summary>
    /// <param name="urgent">The hub stopped answering: scan now even if a sighting of it is still fresh (it may be of the old address), but not more often than <see cref="UrgentGap"/>.</param>
    public async Task LookAsync(Guid hub, bool urgent, CancellationToken ct)
    {
        if (browser is null) return;
        Watch(hub);
        if (!urgent && _cache.Recent(hub).Count > 0) return;
        var gap = urgent ? UrgentGap : BrowseEvery;
        if (!Due(gap)) return;

        await _scan.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another account scanned while this one waited: its answer is this one's.
            if (!Due(gap)) return;
            // Set before the scan: one that fails is not started again at once.
            _lastScanAt = _time.GetUtcNow();
            IReadOnlyList<BlindLanSighting> found;
            try
            {
                found = await browser.BrowseAsync(ScanTime, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "The mDNS browse failed; the hub is looked for again later");
                return;
            }

            var seen = DateTime.UtcNow;
            foreach (var sighting in found)
                if (IsHubSighting(sighting, _hubs.ContainsKey)) _cache.Note(sighting.NodeId, UrlOf(sighting), seen);
        }
        finally
        {
            _scan.Release();
        }
    }

    private bool Due(TimeSpan gap) => _lastScanAt is not { } last || _time.GetUtcNow() - last >= gap;

    /// <summary>A record worth keeping: a watched hub's, serving its sync routes over https, at an IP address of a LAN or VPN (never a host name, which anyone on the network could point anywhere).</summary>
    public static bool IsHubSighting(BlindLanSighting sighting, Func<Guid, bool> isHub) =>
        sighting.NodeId != Guid.Empty && sighting.Https && sighting.ServesSync && isHub(sighting.NodeId)
        && IPAddress.TryParse(sighting.Host.Trim('[', ']'), out _) && ChannelPlanner.IsAutomaticHost(UrlOf(sighting));

    private static string UrlOf(BlindLanSighting s) => $"https://{(s.Host.Contains(':') && !s.Host.StartsWith('[') ? $"[{s.Host}]" : s.Host)}:{s.Port}";
}

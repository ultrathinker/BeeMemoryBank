using System.Net;
using System.Net.Sockets;
using System.Text;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// What an NSD record of <c>_beememorybank._tcp</c> may become on the phone (docs/design/lan-peer-sync.md sections 7 and 16): a sighting of a paired node
/// at an IPv4 address of a LAN or VPN, noted in the <see cref="PeerDiscoveryCache"/> the channel planner reads. Platform-free on purpose: the NSD glue
/// (<c>Platforms/Android/NsdPeerDiscovery.cs</c>) hands over what Android resolved, and this decides what counts. A record is only a candidate: the planner
/// uses it when the peer's TLS anchor and node key match, so a forged record costs a failed attempt, never a wrong peer.
/// </summary>
public static class LanSighting
{
    /// <summary>The DNS-SD service type the desktop node announces (the same as <c>MdnsConstants.ServiceType</c>).</summary>
    public const string ServiceType = "_beememorybank._tcp";

    public const string TxtNodeId = "nodeId";
    public const string TxtHttps = "https";
    public const string TxtSurface = "surface";

    /// <summary>The most services one look resolves: a network that answers with more is not a home or an office with a few computers.</summary>
    public const int MaxServicesPerLook = 32;

    /// <summary>The most addresses one record may add (a computer with Ethernet, Wi-Fi and a VPN).</summary>
    public const int MaxAddressesPerRecord = 4;

    /// <summary>A node seen: who, and the https addresses it answers at.</summary>
    public sealed record Seen(Guid NodeId, IReadOnlyList<string> Urls);

    /// <summary>The TXT attributes as text, keys compared without case (RFC 6763). A flag without a value is an empty string.</summary>
    public static Dictionary<string, string> Attributes(IEnumerable<KeyValuePair<string, byte[]?>> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in raw)
            if (!string.IsNullOrEmpty(key)) result[key] = value is null ? "" : Encoding.UTF8.GetString(value);
        return result;
    }

    /// <summary>
    /// The sighting a resolved record gives, or null when it is not one: no parsable <c>nodeId</c>, a node that is not a paired peer, no <c>https=true</c>,
    /// a <c>surface</c> that does not serve the sync routes, a port that is none, or no IPv4 address of a LAN or VPN. A host name is never taken (the
    /// planner refuses an automatic internet host anyway), and neither is a loopback, link-local, multicast or metadata address.
    /// </summary>
    /// <param name="txt">The TXT attributes (<see cref="Attributes"/>).</param>
    /// <param name="addresses">What the record resolved to; only the IPv4 ones are used.</param>
    /// <param name="port">The SRV port.</param>
    /// <param name="paired">The node ids of the active peers in the whitelist.</param>
    public static Seen? Read(IReadOnlyDictionary<string, string> txt, IEnumerable<IPAddress> addresses, int port, IReadOnlySet<Guid> paired)
    {
        if (!txt.TryGetValue(TxtNodeId, out var id) || !Guid.TryParse(id, out var nodeId) || nodeId == Guid.Empty || !paired.Contains(nodeId)) return null;
        if (!txt.TryGetValue(TxtHttps, out var https) || !bool.TryParse(https, out var secure) || !secure) return null;
        if (!ServesSync(txt.TryGetValue(TxtSurface, out var surface) ? surface : null)) return null;
        if (port is < 1 or > 65535) return null;

        var urls = new List<string>();
        foreach (var ip in addresses)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || !PeerAddressPolicy.IsAllowedAddress(ip)) continue;
            var url = $"https://{ip}:{port}";
            if (!ChannelPlanner.IsAutomaticHost(url) || urls.Contains(url, StringComparer.Ordinal)) continue;
            urls.Add(url);
            if (urls.Count == MaxAddressesPerRecord) break;
        }
        return urls.Count == 0 ? null : new Seen(nodeId, urls);
    }

    /// <summary>Notes a sighting for the planner.</summary>
    public static void Note(PeerDiscoveryCache cache, Seen seen, DateTime nowUtc)
    {
        foreach (var url in seen.Urls) cache.Note(seen.NodeId, url, nowUtc);
    }

    /// <summary>
    /// A record of a build before LAN sync has no <c>surface</c> (its whole front is served); otherwise the sync routes answer when <c>sync</c> or <c>web</c>
    /// is named (the same rule as the desktop's <c>MdnsNodeRecord.ServesSync</c>).
    /// </summary>
    public static bool ServesSync(string? surface) =>
        surface is null || surface.Split(',').Select(s => s.Trim()).Any(s => s.Equals("sync", StringComparison.OrdinalIgnoreCase) || s.Equals("web", StringComparison.OrdinalIgnoreCase));
}

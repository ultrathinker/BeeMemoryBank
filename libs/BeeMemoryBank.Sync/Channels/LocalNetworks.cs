using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BeeMemoryBank.Sync.Channels;

/// <summary>One IPv4 network this computer is on now: its address there and the prefix length; <paramref name="Vpn"/> for a tailnet or VPN address.</summary>
public sealed record LocalSubnet(IPAddress Address, int PrefixLength, bool Vpn)
{
    public bool Contains(IPAddress other)
    {
        if (other.IsIPv4MappedToIPv6) other = other.MapToIPv4();
        if (other.AddressFamily != AddressFamily.InterNetwork || Address.AddressFamily != AddressFamily.InterNetwork) return false;
        var mask = PrefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(32, PrefixLength));
        return (ToUInt(Address) & mask) == (ToUInt(other) & mask);
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}

/// <summary>The networks this computer is on (an interface so the planner's tests can say where the computer is).</summary>
public interface ILocalNetworks
{
    IReadOnlyList<LocalSubnet> Current();
}

/// <summary>
/// The networks as the system reports them, read at most every few seconds. Every interface that is up counts here, a VPN and a virtual adapter too:
/// the question is only "could an address of this subnet be reached from here", not where this node's own listener may answer.
/// </summary>
public sealed class SystemLocalNetworks : ILocalNetworks
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private IReadOnlyList<LocalSubnet> _cached = [];
    private DateTime _readAt = DateTime.MinValue;

    public IReadOnlyList<LocalSubnet> Current()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _readAt < MaxAge) return _cached;
            _cached = Read();
            _readAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private static IReadOnlyList<LocalSubnet> Read()
    {
        var result = new List<LocalSubnet>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }
        foreach (var ni in interfaces)
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var tunnel = ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                    int prefix;
                    try { prefix = u.PrefixLength; } catch (PlatformNotSupportedException) { prefix = 24; }
                    result.Add(new LocalSubnet(u.Address, prefix is > 0 and <= 32 ? prefix : 24, tunnel || ChannelScopes.IsCgnat(u.Address)));
                }
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }
}

/// <summary>A peer's address as an mDNS record (or a phone's Bonjour / NSD browse) showed it on this network.</summary>
public sealed record DiscoveredEndpoint(Guid NodeId, string Url, DateTime SeenAtUtc);

/// <summary>
/// The LAN discovery of the host (docs/design/lan-peer-sync.md 2.5, 16): the desktop node browses mDNS, a phone uses NSD or Bonjour. The sync loop asks it
/// for the addresses a peer was seen at recently, and nudges it when the peer's LAN addresses fail (a DHCP change).
/// </summary>
public interface IPeerDiscovery
{
    /// <summary>Where <paramref name="nodeId"/> was seen in the last <see cref="Freshness"/>.</summary>
    IReadOnlyList<DiscoveredEndpoint> Recent(Guid nodeId);

    /// <summary>Asks for a fresh look soon (the peer's addresses failed). Never blocks.</summary>
    void Prompt();

    /// <summary>How long a sighting counts.</summary>
    static TimeSpan Freshness => TimeSpan.FromMinutes(10);
}

/// <summary>A host without LAN discovery (a server, a blind node, a phone before its port): nothing is seen.</summary>
public sealed class NoPeerDiscovery : IPeerDiscovery
{
    public IReadOnlyList<DiscoveredEndpoint> Recent(Guid nodeId) => [];
    public void Prompt() { }
}

/// <summary>
/// A discovery the host fills (the desktop node's mDNS browser, a phone's NSD or Bonjour): <see cref="Note"/> each sighting, the planner reads
/// <see cref="Recent"/>. In memory and bounded; a sighting older than <see cref="IPeerDiscovery.Freshness"/> is forgotten.
/// </summary>
public class PeerDiscoveryCache : IPeerDiscovery
{
    private const int MaxPerNode = 8;
    private const int MaxNodes = 256;
    private readonly ConcurrentDictionary<Guid, List<DiscoveredEndpoint>> _seen = new();
    private int _prompted;

    public IReadOnlyList<DiscoveredEndpoint> Recent(Guid nodeId)
    {
        if (!_seen.TryGetValue(nodeId, out var list)) return [];
        var cutoff = DateTime.UtcNow - IPeerDiscovery.Freshness;
        lock (list) return list.Where(e => e.SeenAtUtc >= cutoff).ToList();
    }

    /// <summary>A peer was seen at <paramref name="url"/> (made canonical by the policy here; an address it refuses is ignored).</summary>
    public void Note(Guid nodeId, string url, DateTime seenAtUtc)
    {
        if (nodeId == Guid.Empty || PeerAddressPolicy.ForStorage(url) is not { } canonical) return;
        if (_seen.Count >= MaxNodes && !_seen.ContainsKey(nodeId))
        {
            // Full: the nodes whose every sighting is old are forgotten first; still full, the sighting is dropped (the browser notes paired peers only).
            foreach (var (id, old) in _seen)
            {
                bool stale;
                lock (old) stale = old.All(e => seenAtUtc - e.SeenAtUtc > IPeerDiscovery.Freshness);
                if (stale) _seen.TryRemove(id, out _);
            }
            if (_seen.Count >= MaxNodes) return;
        }
        var list = _seen.GetOrAdd(nodeId, _ => new List<DiscoveredEndpoint>());
        lock (list)
        {
            list.RemoveAll(e => e.Url == canonical || seenAtUtc - e.SeenAtUtc > IPeerDiscovery.Freshness);
            list.Add(new DiscoveredEndpoint(nodeId, canonical, seenAtUtc));
            if (list.Count > MaxPerNode) list.RemoveRange(0, list.Count - MaxPerNode);
        }
    }

    public void Prompt() => Interlocked.Exchange(ref _prompted, 1);

    /// <summary>True once after <see cref="Prompt"/> (the browser looks again at once instead of at its regular time).</summary>
    public bool TakePrompt() => Interlocked.Exchange(ref _prompted, 0) == 1;
}

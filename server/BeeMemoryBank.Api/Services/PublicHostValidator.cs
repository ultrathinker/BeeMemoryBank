using System.Net;
using System.Net.Sockets;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Decides whether a hostname resolves to a public, routable address. Used by
/// <c>POST /api/sync/probe-relay</c> so an authenticated peer can't turn this node into an
/// internal-network/cloud-metadata SSRF probe under the guise of "check my public URL".
/// Abstracted behind an interface so tests that route synthetic hostnames through a fake
/// <c>HttpMessageHandler</c> (no real DNS involved) can substitute a permissive stub.
/// </summary>
public interface IPublicHostValidator
{
    Task<bool> IsPublicHostAsync(string host, CancellationToken ct);
}

/// <summary>
/// Real implementation: resolves the host and rejects if resolution fails or ANY resolved
/// address is loopback, private/RFC1918, link-local (which also covers the 169.254.169.254
/// cloud-metadata address), unspecified, or another range that is not globally reachable
/// (<see cref="HostAddressPolicy.IsPublic"/>).
/// </summary>
public sealed class DnsPublicHostValidator : IPublicHostValidator
{
    public async Task<bool> IsPublicHostAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch
        {
            return false; // unresolvable — reject rather than risk a surprising outbound call
        }

        if (addresses.Length == 0) return false;
        return addresses.All(IsPublicAddress);
    }

    private static bool IsPublicAddress(IPAddress ip) => HostAddressPolicy.IsPublic(ip);
}

/// <summary>
/// Which kinds of address a node may be asked to connect to on an administrator's or a peer's word. Two questions, one set of
/// rules: <see cref="IsPublic"/> (the probe relay and the model providers not marked local: only the open internet) and <see cref="IsPossibleHub"/> ("let blind copies call
/// this node": the open internet or the administrator's own network, never this machine). An address that merely wraps an
/// IPv4 one (IPv4-mapped, IPv4-compatible, NAT64 <c>64:ff9b::/96</c>, 6to4 <c>2002::/16</c>) is judged as the IPv4 address it
/// stands for, so no spelling gets a loopback or cloud-metadata address past the check. Both answers also hold the node-wide rule
/// of <see cref="PeerNetworkRules"/> (the one every peer address and every connect step uses), which is where the fixed cloud-metadata
/// addresses are listed (<c>100.100.100.200</c>, <c>192.0.0.192</c>, <c>168.63.129.16</c>, <c>fd00:ec2::/32</c>, <c>fd20:ce::254</c>), so that
/// there is ONE list of them.
/// </summary>
public static class HostAddressPolicy
{
    /// <summary>
    /// Open internet only: not this machine, not link-local, not multicast, not a private LAN range, and nothing else the IANA special-purpose
    /// address registries say is not globally reachable (<see cref="IsSpecialPurpose"/>: the shared address space of carrier NAT and Tailscale,
    /// the documentation and benchmarking ranges, the IETF protocol blocks, IPv6 outside global unicast). Used by the probe relay and by the
    /// model providers that are not marked local.
    /// </summary>
    public static bool IsPublic(IPAddress ip) =>
        ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !IsNeverReachableByACopy(ip) && !IsPrivateNetwork(ip) && !IsSpecialPurpose(ip) && PeerNetworkRules.IsAllowed(ip);

    /// <summary>
    /// An address a blind copy could be told to call: everything except loopback, link-local (169.254.0.0/16 with the cloud
    /// metadata address, fe80::/10), unspecified, multicast and the reserved/broadcast block. The private LAN ranges
    /// (10/8, 172.16/12, 192.168/16, fc00::/7) stay allowed: a pinned hub on a company or home network is a supported setup.
    /// </summary>
    public static bool IsPossibleHub(IPAddress ip) =>
        ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !IsNeverReachableByACopy(ip) && PeerNetworkRules.IsAllowed(ip);

    private static bool IsNeverReachableByACopy(IPAddress ip)
    {
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var v = Unwrap(ip);
        if (IPAddress.IsLoopback(v) || v.Equals(IPAddress.Any)) return true;

        var b = v.GetAddressBytes();
        if (v.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 0                                   // 0.0.0.0/8: "this network"
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 link-local, incl. the cloud metadata address
                || b[0] >= 224;                                // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255
        return b[0] == 0xFF                                    // ff00::/8 multicast
            || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);        // fe80::/10 link-local
    }

    private static bool IsPrivateNetwork(IPAddress ip)
    {
        var v = Unwrap(ip);
        var b = v.GetAddressBytes();
        if (v.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        return (b[0] & 0xFE) == 0xFC;                          // fc00::/7 unique local
    }

    /// <summary>
    /// The ranges of the IANA IPv4 and IPv6 special-purpose address registries that are not globally reachable and are not already refused by
    /// the other rules, judged on the address an IPv4-embedding IPv6 address stands for. Only <see cref="IsPublic"/> asks this:
    /// <see cref="IsPossibleHub"/> keeps its own, wider answer (a hub may sit on any network the administrator runs, a Tailscale one included).
    /// </summary>
    private static bool IsSpecialPurpose(IPAddress ip)
    {
        var v = Unwrap(ip);
        var b = v.GetAddressBytes();
        if (v.AddressFamily == AddressFamily.InterNetwork)
            return (b[0] == 100 && (b[1] & 0xC0) == 64)            // 100.64.0.0/10 shared address space (carrier NAT, Tailscale)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)         // 192.0.0.0/24 IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)         // 192.0.2.0/24 TEST-NET-1
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)       // 192.88.99.0/24 the deprecated 6to4 relay anycast
                || (b[0] == 198 && (b[1] & 0xFE) == 18)            // 198.18.0.0/15 benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)      // 198.51.100.0/24 TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113);      // 203.0.113.0/24 TEST-NET-3

        // The local-use NAT64 prefix 64:ff9b:1::/48 (RFC 8215) may carry the IPv4 address at any of the RFC 6052 positions its operator chose:
        // it passes only when every reading is a public IPv4 address. (64:ff9b::/96, 6to4 and the IPv4-mapped and -compatible forms were
        // unwrapped above.)
        if (b is [0x00, 0x64, 0xFF, 0x9B, 0x00, 0x01, ..])
            return !LocalUseNat64Readings(b).All(IsPublic);

        return (b[0] & 0xE0) != 0x20                               // outside 2000::/3 global unicast: ::/128, ::1, 100::/64 discard, 5f00::/16 SRv6,
                                                                   // fc00::/7, fec0::/10, fe80::/10, ff00::/8 and everything not yet allocated
            || (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0x00) // 2001::/23 IETF protocol assignments: Teredo 2001::/32, benchmarking, ORCHID
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // 2001:db8::/32 documentation
            || (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00); // 3fff::/20 documentation
    }

    /// <summary>The IPv4 address 64:ff9b:1::/48 carries, read at each RFC 6052 position (prefix /48, /56, /64 and /96; the u octet, byte 8, skipped).</summary>
    private static IEnumerable<IPAddress> LocalUseNat64Readings(byte[] b) =>
    [
        new IPAddress(new[] { b[6], b[7], b[9], b[10] }),
        new IPAddress(new[] { b[7], b[9], b[10], b[11] }),
        new IPAddress(new[] { b[9], b[10], b[11], b[12] }),
        new IPAddress(new[] { b[12], b[13], b[14], b[15] }),
    ];

    private static IPAddress Unwrap(IPAddress ip) => PeerNetworkRules.Unwrap(ip);
}

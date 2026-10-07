using System.Net;
using System.Net.Sockets;

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
/// cloud-metadata address), or unspecified.
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
/// rules: <see cref="IsPublic"/> (the probe relay: only the open internet) and <see cref="IsPossibleHub"/> ("let blind copies call
/// this node": the open internet or the administrator's own network, never this machine). An address that merely wraps an
/// IPv4 one (IPv4-mapped, IPv4-compatible, NAT64 <c>64:ff9b::/96</c>, 6to4 <c>2002::/16</c>) is judged as the IPv4 address it
/// stands for, so no spelling gets a loopback or cloud-metadata address past the check.
/// </summary>
public static class HostAddressPolicy
{
    /// <summary>Open internet only: not this machine, not link-local, not multicast, not a private LAN range.</summary>
    public static bool IsPublic(IPAddress ip) =>
        ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !IsNeverReachableByACopy(ip) && !IsPrivateNetwork(ip);

    /// <summary>
    /// An address a blind copy could be told to call: everything except loopback, link-local (169.254.0.0/16 with the cloud
    /// metadata address, fe80::/10), unspecified, multicast and the reserved/broadcast block. The private LAN ranges
    /// (10/8, 172.16/12, 192.168/16, fc00::/7) stay allowed: a pinned hub on a company or home network is a supported setup.
    /// </summary>
    public static bool IsPossibleHub(IPAddress ip) =>
        ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !IsNeverReachableByACopy(ip);

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

    /// <summary>The IPv4 address an IPv6 address only wraps, or the address itself.</summary>
    private static IPAddress Unwrap(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip;
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))
            return new IPAddress(b[12..16]);                                        // 64:ff9b::/96 NAT64
        if (b[..12].All(x => x == 0) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
            return new IPAddress(b[12..16]);                                        // ::a.b.c.d IPv4-compatible (not :: or ::1)
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);            // 2002::/16 6to4
        return ip;
    }
}

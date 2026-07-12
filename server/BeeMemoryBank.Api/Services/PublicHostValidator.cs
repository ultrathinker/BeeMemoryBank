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

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;                                   // 0.0.0.0/8
            if (b[0] == 10) return false;                                  // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;      // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false;                  // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return false;                  // 169.254.0.0/16 (incl. cloud metadata)
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any)) return false;                // ::
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                       // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;       // fe80::/10 link-local
            return true;
        }

        return false; // unknown address family — reject
    }
}

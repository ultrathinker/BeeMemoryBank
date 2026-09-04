using System.Net;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>One parsed <c>BMB_TRUSTED_PROXIES</c> entry: a single address or a CIDR network.</summary>
public sealed record TrustedProxyEntry(IPAddress? Address, IPNetwork? Network, string Raw);

/// <summary>
/// Parses the <c>BMB_TRUSTED_PROXIES</c> list — comma- or whitespace-separated IP addresses and
/// CIDR networks whose <c>X-Forwarded-For</c> header this node will believe.
///
/// <para>
/// Needed because every per-IP rate limit in the product is only as meaningful as the IP it keys
/// on. Under Docker with a published port, the address the API sees is the bridge gateway
/// (<c>172.x.0.1</c>) for EVERY external client, so login, join, node-reset and sync-challenge
/// budgets all collapse into a single global bucket: one anonymous client can exhaust the
/// challenge budget and stop synchronization for every peer in the mesh, or burn the login budget
/// for every user at once. The pre-existing loopback-only trust never helped there — Docker's DNAT
/// arrives on the container's bridge interface, not its loopback, so the header was never believed.
/// </para>
///
/// <para>
/// Trusting a forwarded header is exactly as safe as the hop that sets it: anything that can reach
/// the port from a listed address can name any client IP it likes and shed its own limits. So the
/// list must name the reverse proxy (or the Docker bridge network it arrives from) and nothing
/// wider. Unparsable entries are dropped with a warning rather than failing startup — a typo in a
/// deployment variable must not take the node down, and dropping an entry can only ever make trust
/// narrower.
/// </para>
/// </summary>
public static class TrustedProxyParser
{
    public static IReadOnlyList<TrustedProxyEntry> Parse(string? value, out IReadOnlyList<string> invalid)
    {
        var entries = new List<TrustedProxyEntry>();
        var bad = new List<string>();
        invalid = bad;

        if (string.IsNullOrWhiteSpace(value)) return entries;

        foreach (var token in value.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = token.IndexOf('/');
            if (slash >= 0)
            {
                var addrPart = token[..slash];
                var prefixPart = token[(slash + 1)..];
                if (!IPAddress.TryParse(addrPart, out var netAddr) || !int.TryParse(prefixPart, out var prefix))
                {
                    bad.Add(token);
                    continue;
                }

                // An IPv4-mapped IPv6 base address ("::ffff:172.16.0.0") is really an IPv4 network
                // wearing an IPv6 costume, and masking it as IPv6 is actively dangerous: with the
                // IPv4-style prefix an operator naturally writes (/12), the mask zeroes the 0xffff
                // marker itself and the entry collapses to ::/12 — which contains the whole
                // IPv4-mapped range AND a large slice of public IPv6 space. That is trust granted
                // to the internet by way of a plausible typo, so normalize to IPv4 instead: a
                // prefix of 96 or more is the correct IPv6 spelling and converts exactly, and
                // anything below 96 cannot mean "this IPv4 network" at all and is rejected rather
                // than guessed at.
                if (netAddr.IsIPv4MappedToIPv6)
                {
                    if (prefix >= 96 && prefix <= 128)
                    {
                        netAddr = netAddr.MapToIPv4();
                        prefix -= 96;
                    }
                    else
                    {
                        bad.Add(token);
                        continue;
                    }
                }

                var maxPrefix = netAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                if (prefix < 0 || prefix > maxPrefix)
                {
                    bad.Add(token);
                    continue;
                }

                // IPNetwork's constructor rejects a base address with bits set below the prefix
                // (e.g. "172.17.0.5/16"); normalize instead of dropping the whole entry, since a
                // host address written with a prefix is a common and unambiguous way to say
                // "this network".
                entries.Add(new TrustedProxyEntry(null, new IPNetwork(MaskToPrefix(netAddr, prefix), prefix), token));
                continue;
            }

            if (IPAddress.TryParse(token, out var addr))
            {
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                entries.Add(new TrustedProxyEntry(addr, null, token));
                continue;
            }

            bad.Add(token);
        }

        return entries;
    }

    /// <summary>Zeroes every address bit below <paramref name="prefix"/>.</summary>
    private static IPAddress MaskToPrefix(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsBefore = i * 8;
            if (prefix >= bitsBefore + 8) continue;          // whole byte inside the prefix
            if (prefix <= bitsBefore) { bytes[i] = 0; continue; } // whole byte outside it
            var keep = prefix - bitsBefore;                   // 1..7 leading bits survive
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }
}

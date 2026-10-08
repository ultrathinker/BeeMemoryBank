using System.Net;
using System.Net.Sockets;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// iOS asks "find and connect to devices on your local network?" the first time an app reaches a private address, and every connection
/// fails ("No route to host") while that question is on the screen. A join sends the master password in its first request, so before the
/// join the app waits until the node on the local network can be reached at all: a plain TCP connection, opened and closed, nothing sent
/// over it - the person answers the question in the meantime. Seen on the iPhone with the blind app, whose first load failed exactly so
/// while the prompt was open (reports/ios-device.md).
/// </summary>
public static class LocalNetwork
{
    /// <summary>True for an address iOS treats as the local network: RFC 1918 and 4193, link-local, and *.local names.</summary>
    public static bool IsLocal(string host)
    {
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// Waits until a TCP connection to <paramref name="address"/>'s host and port opens (then closes it at once), trying every
    /// <paramref name="every"/> until <paramref name="patience"/> is over. True when it opened; false when the time ran out - the join then
    /// goes ahead and reports the real failure. Addresses that are not on the local network are not tried.
    /// </summary>
    public static Task<bool> WaitUntilReachableAsync(Uri address, TimeSpan patience, TimeSpan? every = null, CancellationToken ct = default) =>
        IsLocal(address.Host) ? ConnectAsync(address, patience, every, ct) : Task.FromResult(true);

    internal static async Task<bool> ConnectAsync(Uri address, TimeSpan patience, TimeSpan? every = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + patience;
        var pause = every ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            using (var client = new TcpClient(address.HostNameType == UriHostNameType.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork))
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await client.ConnectAsync(address.Host.Trim('[', ']'), address.Port, attempt.Token);
                    return true;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // Not yet: the question may be on the screen, or the node is not up.
                }
            }
            if (DateTime.UtcNow + pause > deadline) return false;
            await Task.Delay(pause, ct);
        }
    }
}

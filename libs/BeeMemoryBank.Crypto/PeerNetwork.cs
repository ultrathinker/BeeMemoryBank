using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace BeeMemoryBank.Crypto;

/// <summary>A connection was refused where the socket is opened: the name resolved to an address (or the socket ended up at one) that a node may not call.</summary>
public sealed class PeerAddressRefusedException() : Exception("The address resolves to a place this node does not call (an unspecified, link-local or cloud-metadata address).");

/// <summary>
/// Which addresses a node may call. The rule is the one that judges an address literal typed as a peer's address (<c>PeerAddressPolicy</c>) and every address a peer's
/// name resolves to when a socket is opened (<see cref="PeerSocketConnector"/>): not an unspecified address, not link-local (where the cloud metadata services live),
/// not those services' fixed addresses and not multicast or broadcast. Loopback and private LAN ranges are allowed: this product's nodes live on LANs.
/// </summary>
public static class PeerNetworkRules
{
    public static bool IsAllowed(IPAddress ip) => Refusal(ip) is null;

    /// <summary>A sentence that says why a connection to <paramref name="ip"/> is refused, or null when it is allowed. It never quotes the address.</summary>
    public static string? Refusal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var v4 = ip.AddressFamily == AddressFamily.InterNetwork;

        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || (v4 && ip.GetAddressBytes()[0] == 0))
            return "0.0.0.0 and :: mean \"this machine, any address\", not a device.";
        if (v4)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return "Link-local addresses (169.254.x.x) are where cloud metadata services live, not devices.";
            if (b is [100, 100, 100, 200]) return "That address is a cloud metadata service, not a device.";
            if (b[0] >= 224) return "Multicast and broadcast addresses are not devices.";
        }
        else if (ip.IsIPv6LinkLocal)
            return "Link-local addresses (fe80::) are not an address a device can be reached at from here.";
        else if (ip.IsIPv6Multicast)
            return "Multicast addresses are not devices.";
        else if (ip.Equals(IPAddress.Parse("fd00:ec2::254")))
            return "That address is a cloud metadata service, not a device.";
        return null;
    }
}

/// <summary>Where a connector may connect: which addresses are allowed, and how a name is resolved (a test passes its own resolver).</summary>
public sealed record PeerNetwork(Func<IPAddress, bool> IsAllowed, Func<string, CancellationToken, Task<IPAddress[]>> Resolve)
{
    /// <summary>What a node dials a peer by: <see cref="PeerNetworkRules.IsAllowed"/> on every address, and the system's resolver.</summary>
    public static readonly PeerNetwork Production = new(PeerNetworkRules.IsAllowed, (host, ct) => Dns.GetHostAddressesAsync(host, ct));
}

/// <summary>
/// The connect step of every HTTP client that dials a peer or the other computer of a join (the sync handler, the hub probe, the join clients, the clients of a typed
/// address): the check of the address is made where the socket is opened, because that is the only place it cannot be walked around - a literal host is judged when the
/// address is typed, but a NAME (<c>https://169.254.169.254.nip.io</c>, or any name an attacker controls) passes that and then resolves to whatever its DNS says, and says
/// something else the next time. So this step resolves the host itself, refuses if ANY address it resolves to is not allowed, connects only to the addresses it has just
/// checked, and checks the address it ended up connected to. A system proxy is never used by a handler that uses this: a proxy would resolve and connect on this node's
/// behalf and the check would judge the proxy instead of the target.
/// </summary>
public static class PeerSocketConnector
{
    /// <exception cref="PeerAddressRefusedException">An address the name resolves to, or the one connected to, is not allowed.</exception>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, PeerNetwork network, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await network.Resolve(host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !network.IsAllowed(a))) throw new PeerAddressRefusedException();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            if (socket.RemoteEndPoint is not IPEndPoint remote || !network.IsAllowed(remote.Address)) throw new PeerAddressRefusedException();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>True when <paramref name="ex"/> or something it wraps is a refusal made by <see cref="ConnectAsync"/>.</summary>
    public static bool IsRefusal(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; ex = ex.InnerException, depth++)
            if (ex is PeerAddressRefusedException) return true;
        return false;
    }
}

/// <summary>
/// The handlers of every HTTP client that dials the other side of a join or a peer's address typed by a person. Each of them follows no redirect, uses no proxy and
/// opens its sockets through <see cref="PeerSocketConnector"/>; <c>PeerHttpArchitectureTests</c> keeps every other construction of a handler out of the code that dials a peer.
/// </summary>
public static class PeerHttp
{
    /// <summary>A handler for a request to an address somebody typed (a join without a code, the probe relay): no redirect, no proxy, a checked connect.</summary>
    public static SocketsHttpHandler NoRedirectHandler(PeerNetwork? network = null)
    {
        var net = network ?? PeerNetwork.Production;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, ct) => PeerSocketConnector.ConnectAsync(context, net, ct),
        };
    }
}

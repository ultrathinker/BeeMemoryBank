using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Makaretu.Dns;

namespace BeeMemoryBank.Mdns;

/// <summary>
/// Opens and closes Makaretu's <see cref="ServiceDiscovery"/> so that a closed one is really gone (BMB-373).
/// </summary>
/// <remarks>
/// <para>
/// Makaretu.Dns.Multicast 0.27.0 subscribes every <see cref="MulticastService"/> to the static
/// <see cref="NetworkChange.NetworkAddressChanged"/> event on Windows and never unsubscribes: <c>Stop()</c> and <c>Dispose()</c>
/// leave the handler behind. The static event then keeps the closed service alive, and whenever a network adapter appears or
/// goes (Wi-Fi, a VPN, WSL, a dock) every such closed service opens a new set of sockets on port 5353 that nothing ever closes.
/// A node that scanned its network once a minute for two days held thousands of closed services; one adapter change opened
/// hundreds of sockets and choked the Windows network stack until a reboot.
/// </para>
/// <para>
/// <see cref="Close"/> removes that handler itself (a delegate to the same method on the same object compares equal, so the
/// event lets it go), disposes the discovery and its service, stops the service once more in case a handler that was already
/// running opened a client, and closes the two unicast sockets <c>Stop()</c> leaves open. The private names are checked by
/// <c>MdnsServiceLifetimeTests</c>: a library upgrade that renames them fails the tests instead of bringing the leak back.
/// </para>
/// </remarks>
public static class MdnsServiceLifetime
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The library's handler of <see cref="NetworkChange.NetworkAddressChanged"/>; null if a library version renamed it.</summary>
    public static MethodInfo? AddressChangedHandler { get; } = typeof(MulticastService).GetMethod(
        "OnNetworkAddressChanged", Private, binder: null, [typeof(object), typeof(EventArgs)], modifiers: null);

    /// <summary>The service's unicast sockets, created with the object and not closed by <c>Stop()</c>; null entries if renamed.</summary>
    public static FieldInfo?[] UnicastClientFields { get; } =
    [
        typeof(MulticastService).GetField("unicastClientIp4", Private),
        typeof(MulticastService).GetField("unicastClientIp6", Private),
    ];

    /// <summary>A new discovery over its own started multicast service, as <c>new ServiceDiscovery()</c> gives. Close it with <see cref="Close"/>, never with Dispose alone.</summary>
    public static ServiceDiscovery Open() => new();

    /// <summary>Closes a discovery made by <see cref="Open"/> so that nothing of it stays alive or keeps a socket. Never throws.</summary>
    public static void Close(ServiceDiscovery? discovery)
    {
        if (discovery is null) return;
        var mdns = discovery.Mdns;
        if (mdns is not null) Unhook(mdns);
        try { discovery.Dispose(); }
        catch { /* best effort: the sockets are closed below as well */ }
        if (mdns is null) return;
        try { mdns.Stop(); }
        catch { /* best effort */ }
        foreach (var field in UnicastClientFields)
        {
            try { (field?.GetValue(mdns) as UdpClient)?.Dispose(); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Removes the service's handler from the static <see cref="NetworkChange.NetworkAddressChanged"/>. False when the handler is unknown or the platform has no such event.</summary>
    public static bool Unhook(MulticastService mdns)
    {
        if (AddressChangedHandler is null) return false;
        try
        {
            var handler = (NetworkAddressChangedEventHandler)Delegate.CreateDelegate(typeof(NetworkAddressChangedEventHandler), mdns, AddressChangedHandler);
            NetworkChange.NetworkAddressChanged -= handler;
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException or MemberAccessException)
        {
            return false;
        }
    }
}

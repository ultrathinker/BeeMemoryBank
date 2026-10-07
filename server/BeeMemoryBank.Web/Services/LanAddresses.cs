using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// This machine's LAN IPv4 addresses: the hosts a join code or the "address to open" on the Admin card names.
/// Skips loopback, tunnel, and common virtual adapters.
/// </summary>
/// <remarks>
/// Deliberately duplicates <c>LocalCaService.GetLanIPv4Addresses</c>'s logic rather than calling it, because that method is
/// private on <c>LocalCaService</c>. Keep the two consistent (same adapter filters) so the hosts shown here always match the
/// leaf certificate's SAN list; a change to one must be made to the other.
/// </remarks>
public static class LanAddresses
{
    public static List<IPAddress> GetLanIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var desc = ni.Description.ToLowerInvariant();
                var name = ni.Name.ToLowerInvariant();
                if (IsVirtualAdapter(desc) || IsVirtualAdapter(name)) continue;

                var ipProperties = ni.GetIPProperties();
                foreach (var unicast in ipProperties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(unicast.Address))
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch
        {
            // Fail-safe: return whatever was collected.
        }
        return addresses;
    }

    private static bool IsVirtualAdapter(string s) =>
        s.Contains("virtual") || s.Contains("vpn") || s.Contains("pseudo") ||
        s.Contains("docker") || s.Contains("hyper-v") || s.Contains("virtualbox") ||
        s.Contains("vmware") || s.Contains("loopback") || s.Contains("vethernet");

    /// <summary>The addresses to open in a browser on another device: <c>https://&lt;ip&gt;:&lt;port&gt;</c>, one per LAN address.</summary>
    public static IReadOnlyList<string> BrowserUrls(int port, IEnumerable<IPAddress> addresses) =>
        addresses.Select(ip => $"https://{ip}:{port}").ToList();
}

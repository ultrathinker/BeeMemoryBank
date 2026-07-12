using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Connect a device" page: shows a QR code encoding <c>https://&lt;lan-ip&gt;:5311</c> (the node's
/// opt-in HTTPS front port, <see cref="HttpsPort"/>) for each enumerated LAN IPv4 address, plus a
/// link to download this node's CA certificate so the device can trust it.
/// </summary>
/// <remarks>
/// The LAN-IP enumeration deliberately duplicates <c>LocalCaService.GetLanIPv4Addresses</c>'s logic
/// rather than calling it: that method is private on <c>LocalCaService</c>, and the task brief
/// forbids modifying <c>LocalCaService.cs</c>. The duplication is kept byte-for-byte consistent
/// (same adapter filters) so the QR's hosts always match the leaf certificate's SAN list.
/// </remarks>
[Authorize]
public class ConnectModel : PageModel
{
    /// <summary>The HTTPS port the node's opt-in front listens on (mirrors NodeFront.HttpsPort).</summary>
    public const int HttpsPort = 5311;

    /// <summary>One entry per reachable LAN IPv4 address of this machine.</summary>
    public IReadOnlyList<LanEndpoint> Endpoints { get; private set; } = Array.Empty<LanEndpoint>();

    /// <summary>True when at least one LAN IPv4 address could be enumerated.</summary>
    public bool HasLanAddress => Endpoints.Count > 0;

    public void OnGet()
    {
        var list = new List<LanEndpoint>();
        foreach (var ip in GetLanIPv4Addresses())
        {
            var url = $"https://{ip}:{HttpsPort}";
            list.Add(new LanEndpoint(ip.ToString(), url, GenerateQrPngDataUri(url)));
        }
        Endpoints = list;
    }

    /// <summary>
    /// Renders <paramref name="payload"/> as a PNG QR code wrapped in a <c>data:image/png;base64</c>
    /// URI suitable for an <c>&lt;img&gt;</c> <c>src</c>. ECC level Q for robustness to partial
    /// obscuring (phone screens / camera glare).
    /// </summary>
    private static string GenerateQrPngDataUri(string payload)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = pngQrCode.GetGraphic(pixelsPerModule: 20);
        return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
    }

    /// <summary>
    /// Enumerates this machine's LAN IPv4 addresses, skipping loopback, tunnel, and common
    /// virtual adapters. Mirrors <c>LocalCaService.GetLanIPv4Addresses</c> exactly so the QR
    /// targets only hosts that are also present in the leaf certificate's SAN list.
    /// </summary>
    private static List<IPAddress> GetLanIPv4Addresses()
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

    /// <summary>One reachable LAN endpoint with its connect URL and QR rendering.</summary>
    public sealed record LanEndpoint(string Ip, string Url, string QrDataUri);
}

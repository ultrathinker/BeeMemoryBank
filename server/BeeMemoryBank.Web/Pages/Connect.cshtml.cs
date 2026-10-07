using System.Net;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Connect a device" page. On the desktop node (Windows app, Mac app, Windows service) it opens the LAN
/// join listener on demand (no restart, so the vault stays unlocked), shows one join code per LAN IPv4
/// address — address, one-time token and the SPKI pin of the node's TLS key, as text and as a QR — and,
/// in the Windows app, adds the firewall rule when the user asks for it (plan section 10). A phone or
/// another computer pins the key from the code before it sends the master password. While "Devices on
/// my network" is on the node is open permanently and the code carries no token.
/// </summary>
/// <remarks>
/// Superadmin only: opening a network listener and handing out join codes is administration. The LAN
/// addresses come from <see cref="LanAddresses"/>, which must agree with the leaf certificate's SAN list.
/// </remarks>
[Authorize(Roles = UserRoles.Superadmin)]
public class ConnectModel(IHttpClientFactory httpClientFactory) : PageModel
{
    /// <summary>What the node says about its LAN listener.</summary>
    public NodeLanClient.LanStatus Lan { get; private set; } = NodeLanClient.LanStatus.Unavailable;

    /// <summary>One entry per reachable LAN IPv4 address of this machine, while the listener is on.</summary>
    public IReadOnlyList<LanEndpoint> Endpoints { get; private set; } = Array.Empty<LanEndpoint>();

    /// <summary>True when at least one LAN IPv4 address could be enumerated.</summary>
    public bool HasLanAddress => Endpoints.Count > 0;

    private NodeLanClient Client => new(httpClientFactory.CreateClient());

    public async Task OnGetAsync(CancellationToken ct) => Show(await Client.GetAsync(ct));

    public async Task OnPostEnableAsync(CancellationToken ct) => Show(await Client.EnableAsync(ct));

    public async Task OnPostDisableAsync(CancellationToken ct) => Show(await Client.DisableAsync(ct));

    public async Task OnPostFirewallAsync(CancellationToken ct) => Show(await Client.AddFirewallRuleAsync(ct));

    private void Show(NodeLanClient.LanStatus status)
    {
        Lan = status;
        Endpoints = BuildEndpoints(status, LanAddresses.GetLanIPv4Addresses());
    }

    /// <summary>The join codes to show for <paramref name="status"/> — none unless the listener is on.</summary>
    public static IReadOnlyList<LanEndpoint> BuildEndpoints(NodeLanClient.LanStatus status, IEnumerable<IPAddress> addresses)
    {
        if (!status.Active || string.IsNullOrEmpty(status.SpkiPin)) return Array.Empty<LanEndpoint>();

        var list = new List<LanEndpoint>();
        foreach (var ip in addresses)
        {
            var url = $"https://{ip}:{status.Port}";
            var code = new JoinCode(url, status.Token, status.SpkiPin).ToString();
            list.Add(new LanEndpoint(ip.ToString(), url, code, GenerateQrPngDataUri(code)));
        }
        return list;
    }

    /// <summary>
    /// Renders <paramref name="payload"/> as a PNG QR code wrapped in a <c>data:image/png;base64</c>
    /// URI suitable for an <c>&lt;img&gt;</c> <c>src</c>. ECC level Q for robustness to partial
    /// obscuring (phone screens / camera glare).
    /// </summary>
    internal static string GenerateQrPngDataUri(string payload)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = pngQrCode.GetGraphic(pixelsPerModule: 20);
        return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
    }

    /// <summary>One reachable LAN endpoint with its join code and the code's QR rendering.</summary>
    public sealed record LanEndpoint(string Ip, string Url, string Code, string QrDataUri);
}

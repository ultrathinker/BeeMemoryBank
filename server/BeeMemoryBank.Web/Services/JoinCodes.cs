using System.Net;
using BeeMemoryBank.Core.Models;
using QRCoder;

namespace BeeMemoryBank.Web.Services;

/// <summary>One reachable LAN endpoint with its join code and the code's QR rendering.</summary>
public sealed record JoinCodeEndpoint(string Ip, string Url, string Code, string QrDataUri);

/// <summary>
/// The join codes the "Connect a device" dialog of Admin &gt; Devices shows: one per LAN IPv4 address of this computer while the temporary door is open - the
/// address, the one-time token and the pin of the node's TLS key, as text and as a QR. A phone or another computer pins the key from the code before it sends the
/// master password.
/// </summary>
public static class JoinCodes
{
    /// <summary>The join codes to show for <paramref name="status"/> - none unless the listener is on.</summary>
    public static IReadOnlyList<JoinCodeEndpoint> BuildEndpoints(NodeLanClient.LanStatus status, IEnumerable<IPAddress> addresses)
    {
        if (!status.Active || string.IsNullOrEmpty(status.SpkiPin)) return Array.Empty<JoinCodeEndpoint>();

        var list = new List<JoinCodeEndpoint>();
        foreach (var ip in addresses)
        {
            var url = $"https://{ip}:{status.Port}";
            var code = new JoinCode(url, status.Token, status.SpkiPin).ToString();
            list.Add(new JoinCodeEndpoint(ip.ToString(), url, code, GenerateQrPngDataUri(code)));
        }
        return list;
    }

    /// <summary>
    /// Renders <paramref name="payload"/> as a PNG QR code wrapped in a <c>data:image/png;base64</c>
    /// URI suitable for an <c>&lt;img&gt;</c> <c>src</c>. ECC level Q for robustness to partial
    /// obscuring (phone screens / camera glare).
    /// </summary>
    public static string GenerateQrPngDataUri(string payload)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = pngQrCode.GetGraphic(pixelsPerModule: 20);
        return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
    }
}

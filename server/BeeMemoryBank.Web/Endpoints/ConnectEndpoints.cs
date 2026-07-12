using Microsoft.AspNetCore.Http;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Minimal endpoint serving this node's CA certificate for the "Connect a device" flow. A device
/// downloads <c>/connect/ca.crt</c> and installs it as a trusted root so it can accept the node's
/// self-signed HTTPS leaf. Intentionally anonymous: the device has not joined/authenticated yet.
/// </summary>
public static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this WebApplication app)
    {
        app.MapGet("/connect/ca.crt", () =>
        {
            // The CA cert is Windows-only (LocalCaService returns null off-Windows). Keep the
            // guard here both to short-circuit cleanly and to satisfy the analyzer that the
            // [SupportedOSPlatform("windows")] LocalCaService is only touched on Windows.
            if (!OperatingSystem.IsWindows())
            {
                return Results.NotFound("CA certificate is only available on Windows nodes.");
            }

            var dataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

            var caService = new LocalCaService(dataPath);
            var der = caService.GetCaCertificateDer();
            if (der is null || der.Length == 0)
            {
                return Results.NotFound("CA certificate is not available on this node.");
            }

            // application/x-x509-ca-cert is the standard MIME type browsers/OSes recognize for a
            // CA install prompt. fileDownloadName makes browsers save/open it as ca.crt.
            return Results.File(der, "application/x-x509-ca-cert", fileDownloadName: "ca.crt");
        });
    }
}

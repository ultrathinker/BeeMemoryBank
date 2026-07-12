using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Detects external IP address using UPnP IGD (Internet Gateway Device) protocol.
/// </summary>
public class UpnpExternalIpProvider : IExternalIpProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UpnpExternalIpProvider>? _logger;
    private readonly int _ssdpTimeoutMs;

    public UpnpExternalIpProvider(HttpClient httpClient, int ssdpTimeoutMs = 2000, ILogger<UpnpExternalIpProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ssdpTimeoutMs = ssdpTimeoutMs;
        _logger = logger;
    }

    public async Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var locationUrl = await DiscoverLocationAsync(cancellationToken);
            if (string.IsNullOrEmpty(locationUrl))
            {
                _logger?.LogWarning("UPnP IGD SSDP discovery timed out or failed to find location.");
                return null;
            }

            return await GetExternalIpFromLocationAsync(locationUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "UPnP external IP query failed");
            return null;
        }
    }

    public async Task<string?> DiscoverLocationAsync(CancellationToken cancellationToken = default)
    {
        var multicastEndPoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        using var udpClient = new UdpClient();
        udpClient.ExclusiveAddressUse = false;
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        // Bind to any local port
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var query = "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    "\r\n";

        var bytes = Encoding.UTF8.GetBytes(query);
        await udpClient.SendAsync(bytes, bytes.Length, multicastEndPoint);

        // Receive response with a timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_ssdpTimeoutMs);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var receiveTask = udpClient.ReceiveAsync(cts.Token);
                var result = await receiveTask;
                var responseText = Encoding.UTF8.GetString(result.Buffer);

                // Parse Location header
                using var reader = new StringReader(responseText);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        var location = line["LOCATION:".Length..].Trim();
                        return location;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation
        }

        return null;
    }

    public async Task<IPAddress?> GetExternalIpFromLocationAsync(string locationUrl, CancellationToken cancellationToken = default)
    {
        // 1. Fetch the XML description
        var xmlContent = await _httpClient.GetStringAsync(locationUrl, cancellationToken);
        var doc = XDocument.Parse(xmlContent);

        // 2. Find service WANIPConnection or WANPPPConnection
        var services = doc.Descendants().Where(d => d.Name.LocalName == "service").ToList();
        XElement? targetService = null;
        string? serviceType = null;

        foreach (var service in services)
        {
            var st = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
            if (st == "urn:schemas-upnp-org:service:WANIPConnection:1" ||
                st == "urn:schemas-upnp-org:service:WANPPPConnection:1")
            {
                targetService = service;
                serviceType = st;
                break;
            }
        }

        if (targetService == null || serviceType == null)
        {
            _logger?.LogWarning("UPnP description XML at {Location} does not contain WANIPConnection:1 or WANPPPConnection:1 service.", locationUrl);
            return null;
        }

        var controlUrl = targetService.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
        if (string.IsNullOrEmpty(controlUrl))
        {
            _logger?.LogWarning("UPnP service {ServiceType} controlURL is missing.", serviceType);
            return null;
        }

        var baseUri = new Uri(locationUrl);
        var controlUri = new Uri(baseUri, controlUrl);

        // 3. Send SOAP POST request to get external IP address
        var soapBody = 
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
            $"  <s:Body>\r\n" +
            $"    <u:GetExternalIPAddress xmlns:u=\"{serviceType}\"></u:GetExternalIPAddress>\r\n" +
            $"  </s:Body>\r\n" +
            $"</s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, controlUri);
        request.Headers.Add("SOAPAction", $"\"{serviceType}#GetExternalIPAddress\"");
        request.Content = new StringContent(soapBody, Encoding.UTF8, "text/xml");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var respDoc = XDocument.Parse(responseContent);

        var ipElement = respDoc.Descendants().FirstOrDefault(d => d.Name.LocalName == "NewExternalIPAddress");
        if (ipElement != null && IPAddress.TryParse(ipElement.Value.Trim(), out var ip))
        {
            return ip;
        }

        _logger?.LogWarning("Could not parse NewExternalIPAddress from SOAP response.");
        return null;
    }
}

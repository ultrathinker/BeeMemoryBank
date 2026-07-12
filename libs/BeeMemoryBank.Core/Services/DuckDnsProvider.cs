using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// DNS provider for DuckDNS service.
/// </summary>
public class DuckDnsProvider : IDdnsProvider
{
    private readonly HttpClient _httpClient;
    private readonly DuckDnsConfig _config;
    private readonly ILogger<DuckDnsProvider>? _logger;

    /// <summary>
    /// Base URL for the DuckDNS update endpoint. Exposed as a property to facilitate unit testing.
    /// </summary>
    public string BaseUrl { get; set; } = "https://www.duckdns.org/update";

    public DuckDnsProvider(HttpClient httpClient, DuckDnsConfig config, ILogger<DuckDnsProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task UpdateAsync(IPAddress ip, CancellationToken cancellationToken = default)
    {
        var ipStr = ip.ToString();
        var url = $"{BaseUrl}?domains={Uri.EscapeDataString(_config.Domain)}&token={Uri.EscapeDataString(_config.Token)}&ip={Uri.EscapeDataString(ipStr)}";

        _logger?.LogInformation("Updating DuckDNS record for domain '{Domain}' to '{IP}'", _config.Domain, ipStr);

        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body = body.Trim();

        if (body.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("DuckDNS update succeeded.");
        }
        else
        {
            _logger?.LogError("DuckDNS update failed. Response body: {Body}", body);
            throw new HttpRequestException($"DuckDNS update failed. Response: {body}");
        }
    }
}

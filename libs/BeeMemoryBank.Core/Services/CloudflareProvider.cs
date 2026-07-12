using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// DNS provider for Cloudflare service.
/// </summary>
public class CloudflareProvider : IDdnsProvider
{
    private readonly HttpClient _httpClient;
    private readonly CloudflareConfig _config;
    private readonly ILogger<CloudflareProvider>? _logger;

    /// <summary>
    /// Base URL for the Cloudflare API v4. Exposed as a property to facilitate unit testing.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.cloudflare.com/client/v4";

    /// <summary>
    /// The HTTP method to use for updating the record. Defaults to PATCH (partial update).
    /// </summary>
    public HttpMethod UpdateMethod { get; set; } = HttpMethod.Patch;

    public CloudflareProvider(HttpClient httpClient, CloudflareConfig config, ILogger<CloudflareProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task UpdateAsync(IPAddress ip, CancellationToken cancellationToken = default)
    {
        var ipStr = ip.ToString();
        var url = $"{BaseUrl.TrimEnd('/')}/zones/{Uri.EscapeDataString(_config.ZoneId)}/dns_records/{Uri.EscapeDataString(_config.RecordId)}";

        _logger?.LogInformation("Updating Cloudflare record {RecordId} in zone {ZoneId} to '{IP}' using {Method}", 
            _config.RecordId, _config.ZoneId, ipStr, UpdateMethod.Method);

        var payload = new Dictionary<string, object>
        {
            { "content", ipStr }
        };

        if (!string.IsNullOrEmpty(_config.Domain))
        {
            payload["name"] = _config.Domain;
        }
        
        if (!string.IsNullOrEmpty(_config.RecordType))
        {
            payload["type"] = _config.RecordType;
        }
        else if (UpdateMethod == HttpMethod.Put)
        {
            // For PUT requests, 'type' is required by Cloudflare API. Deduce from IPAddress type.
            payload["type"] = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "AAAA" : "A";
        }

        if (_config.Ttl.HasValue)
        {
            payload["ttl"] = _config.Ttl.Value;
        }
        if (_config.Proxied.HasValue)
        {
            payload["proxied"] = _config.Proxied.Value;
        }

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(UpdateMethod, url);
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogError("Cloudflare update failed with status {Status}. Response: {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Cloudflare update failed with status {response.StatusCode}. Response: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
        {
            _logger?.LogInformation("Cloudflare update succeeded.");
        }
        else
        {
            _logger?.LogError("Cloudflare update failed. Response body: {Body}", body);
            throw new HttpRequestException($"Cloudflare update failed. Response: {body}");
        }
    }
}

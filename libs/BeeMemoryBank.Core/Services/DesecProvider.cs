using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// DNS provider for deSEC service.
/// </summary>
public class DesecProvider : IDdnsProvider
{
    private readonly HttpClient _httpClient;
    private readonly DesecConfig _config;
    private readonly ILogger<DesecProvider>? _logger;

    /// <summary>
    /// Base URL for the deSEC update endpoint. Exposed as a property to facilitate unit testing.
    /// </summary>
    public string BaseUrl { get; set; } = "https://update.dedyn.io/";

    public DesecProvider(HttpClient httpClient, DesecConfig config, ILogger<DesecProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task UpdateAsync(IPAddress ip, CancellationToken cancellationToken = default)
    {
        var ipStr = ip.ToString();
        var url = $"{BaseUrl}?myip={Uri.EscapeDataString(ipStr)}";

        _logger?.LogInformation("Updating deSEC record for domain '{Domain}' to '{IP}'", _config.Domain, ipStr);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        var authString = $"{_config.Domain}:{_config.Token}";
        var base64Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(authString));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body = body.Trim();

        // deSEC response matches dyndns2 (e.g. good <ip> or nochg <ip>)
        if (body.StartsWith("good", StringComparison.OrdinalIgnoreCase) || 
            body.StartsWith("nochg", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("deSEC update succeeded. Response: {Body}", body);
        }
        else
        {
            _logger?.LogError("deSEC update failed. Response body: {Body}", body);
            throw new HttpRequestException($"deSEC update failed. Response: {body}");
        }
    }
}

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Configuration for the Cloudflare DDNS provider.
/// </summary>
public class CloudflareConfig
{
    public string ZoneId { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;

    // Optional fields to support full PUT requests or customized options
    public string? Domain { get; set; }
    public string? RecordType { get; set; } // e.g. "A" or "AAAA"
    public int? Ttl { get; set; } // 1 for automatic
    public bool? Proxied { get; set; }
}

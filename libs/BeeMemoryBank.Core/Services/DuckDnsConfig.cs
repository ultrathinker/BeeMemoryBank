namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Configuration for the DuckDNS DDNS provider.
/// </summary>
public class DuckDnsConfig
{
    public string Domain { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

namespace BeeMemoryBank.Infrastructure.Ddns;

/// <summary>
/// Configuration for the deSEC DDNS provider.
/// </summary>
public class DesecConfig
{
    public string Domain { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

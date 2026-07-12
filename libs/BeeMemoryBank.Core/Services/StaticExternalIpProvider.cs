using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Simple IP provider that returns a manually configured static IP address.
/// </summary>
public class StaticExternalIpProvider : IExternalIpProvider
{
    private readonly IPAddress? _ipAddress;

    public StaticExternalIpProvider(IPAddress? ipAddress)
    {
        _ipAddress = ipAddress;
    }

    public Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_ipAddress);
    }
}

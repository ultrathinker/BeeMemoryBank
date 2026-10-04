using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Service that detects the current external IP of the system.
/// </summary>
public interface IExternalIpProvider
{
    /// <summary>
    /// Gets the current external IP address.
    /// </summary>
    Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default);
}

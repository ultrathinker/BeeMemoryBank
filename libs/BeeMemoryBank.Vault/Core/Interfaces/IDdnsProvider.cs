using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Service that updates a DNS record with the given IP address.
/// </summary>
public interface IDdnsProvider
{
    /// <summary>
    /// Updates the DNS record with the specified IP address.
    /// </summary>
    Task UpdateAsync(IPAddress ip, CancellationToken cancellationToken = default);
}

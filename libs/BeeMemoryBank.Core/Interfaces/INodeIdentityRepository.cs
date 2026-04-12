using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface INodeIdentityRepository
{
    Task<NodeIdentity?> GetAsync();
    Task CreateAsync(NodeIdentity identity);
    Task StoreSentinelAsync(byte[] sentinelValue);
    Task<byte[]?> GetSentinelAsync();
}

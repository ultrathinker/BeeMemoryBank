using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface INodeIdentityRepository
{
    Task<NodeIdentity?> GetAsync();
    Task CreateAsync(NodeIdentity identity);
    Task StoreSentinelAsync(byte[] sentinelValue);
    Task<byte[]?> GetSentinelAsync();
    Task MarkInitialSyncCompletedAsync();

    /// <summary>
    /// Migrates a legacy v=0 (plaintext) ed25519_private_key row to v=1 (encrypted under master DEK).
    /// Idempotent: only updates rows where v=0; no-op if v=1 already.
    /// </summary>
    Task UpgradePrivateKeyToV1Async(Guid nodeId, byte[] wrappedPrivateKey, byte[] iv);
}

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

    /// <summary>
    /// Admin-configurable web login cookie lifetime. Falls back to (48, true) if the node
    /// identity row doesn't exist yet (pre-init).
    /// </summary>
    Task<(int ExpireHours, bool SlidingExpiration)> GetSessionSettingsAsync();
    Task SetSessionSettingsAsync(int expireHours, bool slidingExpiration);

    /// <summary>
    /// Admin-configurable product name for the web header / tab title, or null when the node
    /// has never set one (the caller then falls back to <see cref="Models.Branding.DefaultName"/>).
    /// Node-local: never synced, so each installation can brand itself independently.
    /// </summary>
    Task<string?> GetBrandNameAsync();

    /// <summary>Stores a custom name; null or blank clears the override and restores the default.</summary>
    Task SetBrandNameAsync(string? brandName);

    /// <summary>
    /// Admin-configurable toggle for whether THIS node generates its own embeddings
    /// (gates <see cref="BeeMemoryBank.Sync.PendingEmbeddingProcessor"/>). There was previously no
    /// way to change this after node init -- see <see cref="Models.NodeIdentity.CanGenerateEmbeddings"/>.
    /// </summary>
    Task SetCanGenerateEmbeddingsAsync(bool enabled);
}

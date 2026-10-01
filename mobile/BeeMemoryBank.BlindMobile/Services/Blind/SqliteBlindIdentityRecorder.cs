using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Records the Android blind node's v=2 identity into local SQLite table <c>tbl_node_identity</c>.
/// Per plan sections 3.5 &amp; 10:
/// 1. NodeId has the UUIDv8 blind marker (<see cref="BlindNodeId"/>).
/// 2. ed25519_private_key_v = 2 (<see cref="NodeIdentityCrypto.ExternalKeyVersion"/>), indicating
///    that the private signing seed lives outside the database (in AndroidKeyStore) and never under DEK.
/// 3. Private key columns remain empty/null; can_generate_embeddings = false.
/// </summary>
public sealed class SqliteBlindIdentityRecorder(INodeIdentityRepository nodeRepo) : IBlindIdentityRecorder
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BlindIdentityRecord?> GetRecordedAsync(CancellationToken ct = default)
    {
        var existing = await nodeRepo.GetAsync();
        return existing is null
            ? null
            : new BlindIdentityRecord(existing.NodeId, existing.Ed25519PublicKey, existing.DisplayName);
    }

    public Task ClearAsync(CancellationToken ct = default) => nodeRepo.ClearAsync();

    public async Task RecordAsync(Guid nodeId, byte[] publicKey, string displayName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existing = await nodeRepo.GetAsync();
            if (existing is not null)
            {
                if (existing.NodeId == nodeId) return;
                throw new InvalidOperationException($"Node already has identity {existing.NodeId}, cannot overwrite with {nodeId}.");
            }

            var identity = new NodeIdentity
            {
                NodeId = nodeId,
                DisplayName = displayName,
                Ed25519PublicKey = publicKey,
                Ed25519PrivateKey = [],
                Ed25519PrivateKeyIV = null,
                Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, // 2
                CanGenerateEmbeddings = false,
                InitialSyncCompleted = false,
                DekEpoch = 1,
                CreatedAt = DateTime.UtcNow
            };
            await nodeRepo.CreateAsync(identity);
        }
        finally
        {
            _gate.Release();
        }
    }
}

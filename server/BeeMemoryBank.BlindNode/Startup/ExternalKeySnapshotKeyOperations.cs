using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindNode.Startup;

/// <summary>
/// <see cref="ISnapshotKeyOperations"/> of the blind node: it signs the packages it serves with its external (v=2) identity key and does
/// nothing else with a key. It never holds a master DEK, so it cannot encrypt a snapshot database (a request for one is refused with the
/// message a locked vault gave before) and has none to decrypt or re-encrypt.
/// </summary>
public sealed class ExternalKeySnapshotKeyOperations(IExternalNodeKey externalKey) : ISnapshotKeyOperations
{
    public byte[] SignWithIdentity(NodeIdentity identity, byte[] payload)
    {
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException(
                $"A blind node signs only with its external identity key (v={NodeIdentityCrypto.ExternalKeyVersion}); the identity row has v={identity.Ed25519PrivateKeyV}.");
        return NodeIdentityCrypto.SignWithExternalSeed(externalKey.ReadSeed, payload);
    }

    public Task<bool> EncryptDatabaseAsync(string dbPath) =>
        throw new InvalidOperationException(
            "Cannot create an encrypted snapshot while the vault is locked. Unlock it first, " +
            "or request an unencrypted snapshot explicitly.");

    public Task DecryptDatabaseIfNeededAsync(string extractedDbPath) =>
        throw new InvalidOperationException(
            "Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");

    public Task ReEncryptDatabaseIfUnlockedAsync(string dbPath) => Task.CompletedTask;

    public (byte[] Wrapped, byte[] Iv, int Version) SealIdentitySeedForRestore(
        string stagedDbPath, Guid newNodeId, byte[] seed, string? masterPassword) =>
        throw new InvalidOperationException(
            "A blind node has no master key to seal a new identity under; it is reseeded from its pair, not restored from a snapshot.");
}

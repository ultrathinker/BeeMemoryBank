using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// <see cref="ISnapshotKeyOperations"/> of a node that holds the master DEK: signs with any identity version, encrypts and decrypts the
/// snapshot database under the session's DEK. The code that used to sit inside <see cref="SnapshotService"/>, moved here unchanged.
/// Full-node (vault) code: a blind node does not contain it.
/// </summary>
/// <param name="session">The session holding the master DEK. Null = no encryption capability at all (test scaffolding).</param>
/// <param name="externalKey">The key a v=2 identity signs with; registered only in the blind role of the full Api.</param>
public sealed class SessionSnapshotKeyOperations(SessionService? session, IExternalNodeKey? externalKey = null) : ISnapshotKeyOperations
{
    /// <summary>
    /// Sign payload with the node's Ed25519 private key. For legacy v=0 rows (plaintext seed)
    /// works without a session. For v=1 rows requires an unlocked SessionService to decrypt
    /// the wrapped seed. Throws InvalidOperationException with a clear message if the version
    /// is v=1 and no unlocked session is available.
    /// </summary>
    public byte[] SignWithIdentity(NodeIdentity nodeIdentity, byte[] payload)
    {
        // A blind node signs with the key it keeps outside the database; it has no DEK to wait for.
        if (nodeIdentity.Ed25519PrivateKeyV == NodeIdentityCrypto.ExternalKeyVersion)
            return NodeIdentityVault.SignWithIdentityOrGetDek(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, () => throw new InvalidOperationException("A v=2 identity is never signed under the DEK."),
                externalKey is null ? null : externalKey.ReadSeed, payload);

        if (nodeIdentity.Ed25519PrivateKeyV == 0)
        {
            // Legacy plaintext: no session needed; pass an empty masterDek (helper does not use
            // it on the v=0 branch).
            return NodeIdentityVault.SignWithIdentity(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, Array.Empty<byte>(), payload);
        }

        if (session is not { IsUnlocked: true })
            throw new InvalidOperationException("Session must be unlocked to sign with v=1 (encrypted) node identity.");
        var masterDek = session.GetMasterDek();
        try
        {
            return NodeIdentityVault.SignWithIdentity(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, masterDek, payload);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    public async Task<bool> EncryptDatabaseAsync(string dbPath)
    {
        if (session is { IsUnlocked: false })
            throw new InvalidOperationException(
                "Cannot create an encrypted snapshot while the vault is locked. Unlock it first, " +
                "or request an unencrypted snapshot explicitly.");

        if (session is null) return false;
        var masterDek = session.GetMasterDek();
        try
        {
            await SnapshotService.EncryptDbFileAsync(dbPath, masterDek);
            return true;
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    public async Task DecryptDatabaseIfNeededAsync(string extractedDbPath)
    {
        if (session is not { IsUnlocked: true })
            throw new InvalidOperationException(
                "Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");

        var masterDek = session.GetMasterDek();
        try
        {
            await SnapshotService.DecryptDbFileAsync(extractedDbPath, masterDek);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    public async Task ReEncryptDatabaseIfUnlockedAsync(string dbPath)
    {
        if (session is not { IsUnlocked: true }) return;
        var reEncDek = session.GetMasterDek();
        try
        {
            await SnapshotService.EncryptDbFileAsync(dbPath, reEncDek);
        }
        finally
        {
            Array.Clear(reEncDek);
        }
    }
}

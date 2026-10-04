using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The key-dependent steps of building and opening a snapshot, split out of <see cref="SnapshotService"/> so that the part of the
/// service a blind node uses (build, sign, serve, extract and verify ciphertext packages) holds no code that touches the master key.
/// A full node supplies <c>SessionSnapshotKeyOperations</c> (signs with any identity version, encrypts and decrypts the snapshot
/// database under the master DEK); a blind node supplies a signer that only has its external v=2 key and refuses the rest, because
/// it never holds a master DEK and a snapshot it serves is never encrypted under one.
/// </summary>
public interface ISnapshotKeyOperations
{
    /// <summary>Signs <paramref name="payload"/> with this node's identity key, whatever the version of the identity row.</summary>
    byte[] SignWithIdentity(NodeIdentity identity, byte[] payload);

    /// <summary>
    /// Encrypts the snapshot database <paramref name="dbPath"/> in place under the master DEK. Throws when the vault is locked rather
    /// than silently leaving the file plain. Returns whether the file was encrypted.
    /// </summary>
    Task<bool> EncryptDatabaseAsync(string dbPath);

    /// <summary>Decrypts the extracted snapshot database <paramref name="extractedDbPath"/> in place when it is encrypted; no-op when it is plain.</summary>
    Task DecryptDatabaseIfNeededAsync(string extractedDbPath);

    /// <summary>
    /// Encrypts <paramref name="dbPath"/> in place when the vault is unlocked (a filtered variant keeps the encryption of the archive it
    /// was made from); does nothing when it is locked.
    /// </summary>
    Task ReEncryptDatabaseIfUnlockedAsync(string dbPath);
}

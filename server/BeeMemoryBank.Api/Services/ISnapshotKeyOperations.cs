using BeeMemoryBank.Core.Models;
using Microsoft.Data.Sqlite;

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

    /// <summary>
    /// Seals <paramref name="seed"/>, the Ed25519 seed of the identity a standalone restore is about to give the node, for storage in
    /// the staged (already decrypted) database <paramref name="stagedDbPath"/> under <paramref name="newNodeId"/>. Returns what to write
    /// into <c>tbl_node_identity</c>: the wrapped seed, its IV and the key version — the same shape initialization writes. Throws
    /// <see cref="InvalidOperationException"/>, before anything is replaced, when no master key that opens the staged database can be found.
    /// </summary>
    /// <param name="masterPassword">The password the restore was confirmed with; opens the staged key slots when the session's own key does not match them.</param>
    (byte[] Wrapped, byte[] Iv, int Version) SealIdentitySeedForRestore(
        string stagedDbPath, Guid newNodeId, byte[] seed, string? masterPassword);

    /// <summary>
    /// Seals the join snapshot's database <paramref name="dbPath"/> in place into a BMBCH1 container (docs/design/step-a-spec.md 11) under the
    /// master DEK, so that after activation a joiner receives no plain SQLite file. Throws while the vault is locked. A blind node never serves
    /// a join snapshot and does not implement it.
    /// </summary>
    Task SealJoinDatabaseAsync(string dbPath) => throw new NotSupportedException("This node cannot seal a join snapshot.");

    /// <summary>The fingerprint of the master DEK a join snapshot is sealed under, or null while the vault is locked (spec 11 step 2, the join cache key).</summary>
    string? GetJoinKeyFingerprint() => throw new NotSupportedException("This node cannot seal a join snapshot.");

    // ---- Encrypted ("sealed") node, docs/design/step-b-spec.md 8.3, 8.4, 10.4 step 3, 10.7. A blind node implements none of these.

    /// <summary>
    /// As <see cref="EncryptDatabaseAsync(string)"/>. With <paramref name="wipePlain"/> the plain staging file is overwritten with random
    /// bytes, flushed and removed (<c>SecureFileWipe</c>, spec 10.6) before the container takes its name, instead of being truncated in place
    /// (which leaves the plain blocks on the disk): the pre-encryption snapshot (spec 10.4 step 3). <paramref name="afterOverwrite"/> is the
    /// test seam of the wipe.
    /// </summary>
    Task<bool> EncryptDatabaseAsync(string dbPath, bool wipePlain, Action<string>? afterOverwrite) =>
        wipePlain ? throw new NotSupportedException("This node cannot wipe a plain staging copy.") : EncryptDatabaseAsync(dbPath);

    /// <summary>
    /// On a sealed node: builds the BMBDB2 container of the sealed staging copy <paramref name="sealedDbPath"/> from its plain image (read page
    /// by page with <paramref name="pageKey"/>, in memory only) under the master DEK and writes it over the staging path. Throws while the vault
    /// is locked. The caller clears <paramref name="pageKey"/>. Returns whether the file was encrypted.
    /// </summary>
    Task<bool> EncryptSealedDatabaseAsync(string sealedDbPath, byte[] pageKey) =>
        throw new NotSupportedException("This node cannot encrypt a sealed snapshot database.");

    /// <summary>As <see cref="SealJoinDatabaseAsync"/> for a sealed staging copy: the BMBCH1 container is streamed from its plain image, no plain file exists.</summary>
    Task SealJoinSealedDatabaseAsync(string sealedDbPath, byte[] pageKey) =>
        throw new NotSupportedException("This node cannot seal a join snapshot.");

    /// <summary>A copy of the master DEK the snapshot containers of this node are made under; the caller clears it. Throws while the vault is locked.</summary>
    byte[] CopyMasterDek() => throw new NotSupportedException("This node holds no master key.");

    /// <summary>
    /// The master DEK of the staged database (the one its key slots hand out at the next unlock), or null when none is found; the caller clears it.
    /// The staged database is opened with <paramref name="openStaged"/>. A restore on a sealed node needs it for the boot store (spec 8.4).
    /// </summary>
    byte[]? FindStagedMasterDek(string stagedDbPath, string? masterPassword, Func<string, SqliteConnection> openStaged) =>
        throw new NotSupportedException("This node cannot restore into a sealed database.");

    /// <summary>
    /// As <see cref="SealIdentitySeedForRestore(string, Guid, byte[], string?)"/> for a sealed node: the staged database is opened with
    /// <paramref name="openStaged"/> (keyed with the vault's page key), never with a plain connection.
    /// </summary>
    (byte[] Wrapped, byte[] Iv, int Version) SealIdentitySeedForRestore(
        string stagedDbPath, Guid newNodeId, byte[] seed, string? masterPassword, Func<string, SqliteConnection> openStaged) =>
        throw new NotSupportedException("This node cannot restore into a sealed database.");
}

using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Data.Sqlite;

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
            try
            {
                await SnapshotService.DecryptDbFileAsync(extractedDbPath, masterDek);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // An encrypted database must be opened before its key slots can be read. A password cannot
                // recover a snapshot encrypted under another vault's master key.
                throw new InvalidOperationException(
                    "This snapshot was made by a different vault (another master key) and cannot be opened here. Nothing was changed.",
                    ex);
            }
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

    public (byte[] Wrapped, byte[] Iv, int Version) SealIdentitySeedForRestore(
        string stagedDbPath, Guid newNodeId, byte[] seed, string? masterPassword)
    {
        // The key that has to wrap the seed is the one the restored database will hand out at its next unlock — the key its own
        // slots hold — not necessarily the one this session holds.
        var dek = FindStagedMasterDek(stagedDbPath, masterPassword)
            ?? throw new InvalidOperationException(
                "This snapshot cannot be restored: the master password does not open its key slots, so the node's new identity " +
                "could not be stored under the snapshot's master key. Nothing was changed.");
        try
        {
            var (wrapped, iv) = NodeIdentityVault.EncryptPrivateKey(seed, dek, newNodeId);
            return (wrapped, iv, 1);
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    // Same bounds SessionService enforces before it runs a key slot's KDF: a snapshot's slots are data from another database and
    // choose their own cost.
    private const int MinArgonMemory = 32768;
    private const int MinArgonIterations = 2;
    private const int MaxArgonMemory = 1_048_576;
    private const int MaxArgonIterations = 20;
    private const int MaxArgonParallelism = 16;

    /// <summary>
    /// The master DEK of the staged database, or null when none is found. Tried in this order: the session's key when it opens the
    /// staged sentinel (it decrypted the archive, so this is the usual case); the staged key slots opened with the restore password;
    /// the session's key when the database has no sentinel to check it against (a vault from before sentinels — the same trust an
    /// unlock places in it). Whatever is returned is the caller's to wipe.
    /// </summary>
    private byte[]? FindStagedMasterDek(string stagedDbPath, string? masterPassword)
    {
        // Pooling=False: the staged file is moved over the live database moments later.
        using var conn = new SqliteConnection($"Data Source={stagedDbPath.Replace("'", "''")};Pooling=False");
        conn.Open();

        byte[]? sentinel;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sentinel_value FROM tbl_node_identity LIMIT 1";
            sentinel = cmd.ExecuteScalar() as byte[];
        }

        if (sentinel is not null && session is { IsUnlocked: true })
        {
            var current = session.GetMasterDek();
            if (MasterKeyManager.VerifySentinel(sentinel, current)) return current;
            Array.Clear(current);
        }

        // This fallback remains for an already-readable legacy database whose sentinel does not match the
        // current session key. It cannot help with an encrypted snapshot from another vault: decryption happens first.
        if (!string.IsNullOrEmpty(masterPassword))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism " +
                "FROM tbl_key_slot WHERE salt IS NOT NULL AND argon_memory IS NOT NULL ORDER BY slot_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int memory = reader.GetInt32(3), iterations = reader.GetInt32(4), parallelism = reader.GetInt32(5);
                if (memory < MinArgonMemory || iterations < MinArgonIterations
                    || memory > MaxArgonMemory || iterations > MaxArgonIterations || parallelism > MaxArgonParallelism)
                    continue;

                byte[]? kek = null;
                byte[]? dek = null;
                try
                {
                    kek = KeyDerivation.DeriveKek(masterPassword, (byte[])reader["salt"], memory, iterations, parallelism);
                    dek = MasterKeyManager.UnwrapMasterDek((byte[])reader["encrypted_master_dek"], (byte[])reader["iv"], kek);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Not this slot's password.
                }
                finally
                {
                    if (kek is not null) Array.Clear(kek);
                }

                if (dek is null) continue;
                if (sentinel is null || MasterKeyManager.VerifySentinel(sentinel, dek)) return dek;
                Array.Clear(dek);
            }
        }

        // Last: a database with no sentinel to check against. A password slot was tried first, since that is what an unlock uses.
        if (sentinel is null && session is { IsUnlocked: true })
            return session.GetMasterDek();
        return null;
    }
}

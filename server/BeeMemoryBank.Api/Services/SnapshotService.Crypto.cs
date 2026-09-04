using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class SnapshotService
{
    // Domain separation tags. These prepend the signed bytes so a signature produced for
    // one purpose can NEVER verify against a different purpose, even if the underlying
    // hashes happen to collide. Forms a "fail-closed" structural defense against verifier
    // confusion bugs in future code.
    //
    // EMBEDDED tag — for `manifest.json.sig` inside tar.gz. Signs the manifest bytes only;
    // file integrity follows transitively from manifest's per-file SHA256 entries.
    //
    // SIDECAR tag — for `<file>.tar.gz.sig` next to the archive. Signs SHA256(manifest||file).
    // Used by sync-export RestoreForJoinAsync.
    //
    // Format: ASCII tag + single 0x00 separator + payload. The 0x00 prevents any
    // collision via prefix-extension since 0x00 cannot appear in our ASCII tag alphabet.
    private static readonly byte[] DomainTagEmbedded = "BMB-MANIFEST-V1\0"u8.ToArray();
    private static readonly byte[] DomainTagSidecar  = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    private static readonly byte[] DbEncryptionAad = "bmb-snap-db-v1"u8.ToArray();
    private static readonly byte[] DbEncryptionAadV2 = "bmb-snap-db-v2"u8.ToArray();

    // internal, not private: the integration tests read what a snapshot actually contains by
    // decrypting the archived database the same way restore does (InternalsVisibleTo in the csproj).
    internal async Task DecryptDbIfNeededAsync(string extractedDbPath)
    {
        var probe = new byte[Math.Min(64, new FileInfo(extractedDbPath).Length)];
        await using (var probeStream = File.OpenRead(extractedDbPath))
        {
            await probeStream.ReadExactlyAsync(probe, 0, probe.Length);
        }
        if (!IsDbEncrypted(probe))
            return;

        if (_sessionService is not { IsUnlocked: true })
            throw new InvalidOperationException(
                "Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");

        var masterDek = _sessionService.GetMasterDek();
        try
        {
            await DecryptDbFileAsync(extractedDbPath, masterDek);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    /// <summary>
    /// Sign payload with the node's Ed25519 private key. For legacy v=0 rows (plaintext seed)
    /// works without a session. For v=1 rows requires an unlocked SessionService to decrypt
    /// the wrapped seed. Throws InvalidOperationException with a clear message if the version
    /// is v=1 and no unlocked session is available.
    /// </summary>
    private byte[] SignWithIdentityAuto(NodeIdentity nodeIdentity, byte[] payload)
    {
        if (nodeIdentity.Ed25519PrivateKeyV == 0)
        {
            // Legacy plaintext: no session needed; pass an empty masterDek (helper does not use
            // it on the v=0 branch).
            return NodeIdentityCrypto.SignWithIdentity(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, Array.Empty<byte>(), payload);
        }

        if (_sessionService is not { IsUnlocked: true })
            throw new InvalidOperationException("Session must be unlocked to sign with v=1 (encrypted) node identity.");
        var masterDek = _sessionService.GetMasterDek();
        try
        {
            return NodeIdentityCrypto.SignWithIdentity(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, masterDek, payload);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    internal static bool IsDbEncrypted(byte[] blob)
    {
        return IsDbEncryptedV2(blob) || IsDbEncryptedV1(blob);
    }

    internal static bool IsDbEncryptedV2(byte[] blob)
    {
        return blob.Length >= DbEncryptionOverheadV2
               && Encoding.ASCII.GetString(blob, 0, 6) == DbEncryptionMagicV2;
    }

    internal static bool IsDbEncryptedV1(byte[] blob)
    {
        return blob.Length >= DbEncryptionOverheadV1
               && Encoding.ASCII.GetString(blob, 0, 6) == DbEncryptionMagicV1;
    }

    internal static async Task EncryptDbFileAsync(string dbPath, byte[] masterDek)
    {
        var dbBytes = await File.ReadAllBytesAsync(dbPath);
        try
        {
            if (dbBytes.Length > MaxEncryptableDbSize)
                throw new InvalidOperationException(
                    $"Database file is {dbBytes.Length / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit. Use a smaller database.");
            var salt = SecureRandom.GetBytes(16);
            var snapDek = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek, 32, salt, DbEncryptionAadV2);
            try
            {
                var iv = SecureRandom.GetBytes(12);
                var ct = new byte[dbBytes.Length];
                var tag = new byte[16];
                using (var gcm = new AesGcm(snapDek, 16))
                {
                    gcm.Encrypt(iv, dbBytes, ct, tag, DbEncryptionAadV2);
                }
                await using var fs = File.Create(dbPath);
                fs.Write(Encoding.ASCII.GetBytes(DbEncryptionMagicV2));
                fs.Write(salt);
                fs.Write(iv);
                fs.Write(tag);
                fs.Write(ct);
            }
            finally
            {
                Array.Clear(snapDek);
            }
        }
        finally
        {
            Array.Clear(dbBytes);
        }
    }

    internal static async Task DecryptDbFileAsync(string dbPath, byte[] masterDek)
    {
        var blob = await File.ReadAllBytesAsync(dbPath);
        if (!IsDbEncrypted(blob))
            return;

        byte[] pt;
        if (IsDbEncryptedV2(blob))
        {
            var salt = blob[6..22];
            var iv = blob[22..34];
            var tag = blob[34..50];
            var ct = blob[50..];
            var snapDek = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek, 32, salt, DbEncryptionAadV2);
            try
            {
                pt = new byte[ct.Length];
                using var gcm = new AesGcm(snapDek, 16);
                gcm.Decrypt(iv, ct, tag, pt, DbEncryptionAadV2);
            }
            finally
            {
                Array.Clear(snapDek);
            }
        }
        else
        {
            var iv = blob[6..18];
            var tag = blob[18..34];
            var ct = blob[34..];
            pt = new byte[ct.Length];
            using var gcm = new AesGcm(masterDek, 16);
            gcm.Decrypt(iv, ct, tag, pt, DbEncryptionAad);
        }

        await File.WriteAllBytesAsync(dbPath, pt);
        Array.Clear(pt);
        Array.Clear(blob);
    }

    public static byte[] BuildSigPayloadEmbedded(byte[] manifestBytes)
    {
        var buf = new byte[DomainTagEmbedded.Length + manifestBytes.Length];
        Buffer.BlockCopy(DomainTagEmbedded, 0, buf, 0, DomainTagEmbedded.Length);
        Buffer.BlockCopy(manifestBytes, 0, buf, DomainTagEmbedded.Length, manifestBytes.Length);
        return buf;
    }

    private static async Task<byte[]> ComputeSignaturePayloadAsync(byte[] manifestBytes, string tarGzPath, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(DomainTagSidecar);
        hasher.AppendData(manifestBytes);
        await using var fs = File.OpenRead(tarGzPath);
        var buffer = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return hasher.GetHashAndReset();
    }
}

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

// Full node only: the snapshot database encryption under the master DEK (v1 and v2 layouts). The signature framing every node
// shares is in SnapshotService.Signature.cs; the key-dependent steps are reached through ISnapshotKeyOperations.
public partial class SnapshotService
{
    private const string DbEncryptionMagicV1 = "BMBDB1";
    private const string DbEncryptionMagicV2 = "BMBDB2";
    private const int DbEncryptionOverheadV1 = 6 + 12 + 16;
    private const int DbEncryptionOverheadV2 = 6 + 16 + 12 + 16;
    private const long MaxEncryptableDbSize = 2L * 1024 * 1024 * 1024;

    private static readonly byte[] DbEncryptionAad = "bmb-snap-db-v1"u8.ToArray();
    private static readonly byte[] DbEncryptionAadV2 = "bmb-snap-db-v2"u8.ToArray();

    // internal, not private: the integration tests read what a snapshot actually contains by
    // decrypting the archived database the same way restore does (InternalsVisibleTo in the csproj).
    internal async Task DecryptDbIfNeededAsync(string extractedDbPath)
    {
        if (!IsDbEncrypted(await ReadEncryptionProbeAsync(extractedDbPath)))
            return;

        // No key operations at all (test scaffolding) is the same as a locked vault: the file stays unreadable.
        if (_keys is null)
            throw new InvalidOperationException(
                "Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");
        await _keys.DecryptDatabaseIfNeededAsync(extractedDbPath);
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

    /// <summary>First 64 bytes (or the whole file, if smaller) of a snapshot database —
    /// enough for the <see cref="IsDbEncrypted"/> magic check without loading the file.</summary>
    private static async Task<byte[]> ReadEncryptionProbeAsync(string dbPath)
    {
        var probe = new byte[Math.Min(64, new FileInfo(dbPath).Length)];
        await using (var probeStream = File.OpenRead(dbPath))
        {
            await probeStream.ReadExactlyAsync(probe, 0, probe.Length);
        }
        return probe;
    }

    // internal, not private: the size-limit tests inject a tiny limit instead of a 2 GB fixture file.
    internal static void EnsureDbSizeWithinEncryptableLimit(long fileLength, long maxDbSize = MaxEncryptableDbSize)
    {
        if (fileLength > maxDbSize)
            throw new InvalidOperationException(
                $"Database file is {fileLength / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit. Use a smaller database.");
    }

    internal static async Task EncryptDbFileAsync(string dbPath, byte[] masterDek, long maxDbSize = MaxEncryptableDbSize)
    {
        // Size check BEFORE reading into memory: a database over the limit must fail with this
        // clear error, not with OutOfMemoryException from ReadAllBytesAsync. (The old guard
        // compared dbBytes.Length - an int, so at most int.MaxValue - against a 2 GiB long:
        // always false, and it only ran after the file had been read anyway.)
        EnsureDbSizeWithinEncryptableLimit(new FileInfo(dbPath).Length, maxDbSize);

        var dbBytes = await File.ReadAllBytesAsync(dbPath);
        try
        {
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

    internal static async Task DecryptDbFileAsync(string dbPath, byte[] masterDek, long maxDbSize = MaxEncryptableDbSize)
    {
        // An unencrypted database passes through untouched, so the size limit only applies to an
        // encrypted one: probe the header first, then enforce the limit BEFORE the whole blob is
        // read into memory (an oversized encrypted blob would otherwise OOM in ReadAllBytesAsync).
        if (!IsDbEncrypted(await ReadEncryptionProbeAsync(dbPath)))
            return;

        EnsureDbSizeWithinEncryptableLimit(new FileInfo(dbPath).Length, maxDbSize);

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

}

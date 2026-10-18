using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
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
    /// <remarks>
    /// On an encrypted node the container is opened in memory and the file is replaced by a sealed staging copy (encrypted with this node's page
    /// key), never by a plain image (spec 8.4).
    /// </remarks>
    internal async Task DecryptDbIfNeededAsync(string extractedDbPath, CancellationToken ct = default)
    {
        if (!IsDbEncrypted(await ReadEncryptionProbeAsync(extractedDbPath)))
            return;

        // No key operations at all (test scaffolding) is the same as a locked vault: the file stays unreadable.
        if (_keys is null)
            throw new InvalidOperationException(
                "Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");

        if (_connFactory.IsSealed)
        {
            await DecryptToSealedStagingAsync(extractedDbPath, ct);
            return;
        }

        await _keys.DecryptDatabaseIfNeededAsync(extractedDbPath, ct);
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

    internal static async Task EncryptDbFileAsync(string dbPath, byte[] masterDek, long maxDbSize = MaxEncryptableDbSize, long? freeMemoryBytes = null,
        CancellationToken ct = default, Func<FileStream, Stream>? wrapWrite = null)
    {
        // Size check BEFORE reading into memory: a database over the limit must fail with this
        // clear error, not with OutOfMemoryException from ReadAllBytesAsync. (The old guard
        // compared dbBytes.Length - an int, so at most int.MaxValue - against a 2 GiB long:
        // always false, and it only ran after the file had been read anyway.)
        var length = new FileInfo(dbPath).Length;
        EnsureDbSizeWithinEncryptableLimit(length, maxDbSize);

        // One buffer: the container codec reads the file into the place its ciphertext takes and encrypts it there (BMB-304), and says how much memory
        // it needs before it takes any.
        byte[] container;
        await using (var plain = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            container = await Bmbdb2Container.EncryptAsync(plain, length, masterDek, ct, freeMemoryBytes);
        // The file that held the plain image is replaced by the container only when the container is whole on the disk: a full volume or a cancelled
        // write leaves the file as it was, never a half-written one (BMB-304 fix round).
        await OwnerOnlyFile.ReplaceAsync(dbPath, container, ct, wrapWrite: wrapWrite);
    }

    internal static async Task DecryptDbFileAsync(string dbPath, byte[] masterDek, long maxDbSize = MaxEncryptableDbSize, long? freeMemoryBytes = null,
        CancellationToken ct = default, Func<FileStream, Stream>? wrapWrite = null)
    {
        // An unencrypted database passes through untouched, so the size limit only applies to an
        // encrypted one: probe the header first, then enforce the limit BEFORE the whole blob is
        // read into memory (an oversized encrypted blob would otherwise OOM in ReadAllBytesAsync).
        if (!IsDbEncrypted(await ReadEncryptionProbeAsync(dbPath)))
            return;

        var length = new FileInfo(dbPath).Length;
        EnsureDbSizeWithinEncryptableLimit(length, maxDbSize);

        // Opened in place (BMB-304): one buffer holds the container and then the image; the file is rewritten from that window and the buffer is cleared.
        DecryptedImage image;
        try
        {
            await using var container = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            image = await Bmbdb2Container.OpenAsync(container, length, masterDek, ct, freeMemoryBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // A cut-off or malformed container: said as what it is, with nothing of the exception's text (the endpoint answers 400 with it). A container that
            // does not authenticate is a CryptographicException, which the key operations word as "made by a different vault".
            throw new SnapshotContainerException(ex);
        }
        using (image)
        {
            // The authenticated image replaces the container through an owner-only temp file and a rename: the file that held the container stays whole
            // until the plain image is whole next to it, and the temp file (plain bytes) is wiped, not just deleted, when the write fails.
            await OwnerOnlyFile.ReplaceAsync(dbPath, image.Memory, ct, discardTemp: p => SecureFileWipe.TryWipe(p), wrapWrite: wrapWrite);
        }
    }

}

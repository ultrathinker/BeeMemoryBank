using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// What a restore on an encrypted node does to the boot store around the swap of the database file (docs/design/step-b-spec.md 8.4): before it,
/// the doors of the staged database and the wrapper of its master DEK are written as pending copies (so the restored passwords open the restored
/// file after a crash at any point); after it, the boot store is rewritten from the database that is there now. Track 2's
/// <see cref="IUnlockMaterialMirror"/> does the writing; see <see cref="MirrorRestoreHooks"/>.
/// </summary>
public interface ISealedRestoreHooks
{
    /// <summary>Before the staged database replaces the live one. <paramref name="staged"/> is open on the staged file (keyed); <paramref name="stagedMasterDek"/> is the master DEK its key slots hand out.</summary>
    Task BeforeSwapAsync(SqliteConnection staged, byte[] stagedMasterDek, CancellationToken ct);

    /// <summary>After the swap, and also when it failed: the boot store follows whichever database is there.</summary>
    Task AfterSwapAsync(CancellationToken ct);
}

/// <summary><see cref="ISealedRestoreHooks"/> over the unlock-material mirror (spec 4.3, 8.4).</summary>
public sealed class MirrorRestoreHooks(IUnlockMaterialMirror mirror) : ISealedRestoreHooks
{
    public async Task BeforeSwapAsync(SqliteConnection staged, byte[] stagedMasterDek, CancellationToken ct)
    {
        await mirror.AddDbKeyWrapperAsync(stagedMasterDek, ct);

        foreach (var s in staged.Query<(long SlotId, string SlotType, byte[] Wrapped, byte[] Iv, byte[]? Salt, long? Mem, long? It, long? Par)>(
                     "SELECT slot_id, slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot"))
            await mirror.AddPendingDoorAsync(new PendingDoor("slot", s.SlotId, s.SlotType, s.Wrapped, s.Iv, s.Salt,
                (int?)s.Mem, (int?)s.It, (int?)s.Par, null, null, null), ct);

        foreach (var a in staged.Query<(long Id, string KeyHash, byte[] Wrapped, byte[] Iv, byte[]? Salt, long KdfVersion)>(
                     "SELECT id, key_hash, encrypted_dek, dek_iv, salt, kdf_version FROM tbl_agent WHERE encrypted_dek IS NOT NULL AND status = 'A'"))
            await mirror.AddPendingDoorAsync(new PendingDoor("agent", a.Id, null, a.Wrapped, a.Iv, a.Salt, null, null, null, a.KeyHash, (int)a.KdfVersion, null), ct);
    }

    public Task AfterSwapAsync(CancellationToken ct) => mirror.RewriteAsync(ct);
}

public partial class SnapshotService
{
    /// <summary>
    /// Set once at composition on an encrypted node (the sealed vault's registration, track 3): the boot store hooks of a restore. Without them an
    /// encrypted node refuses to restore, because the restored doors would be missing from the boot store and a restart could not unlock.
    /// </summary>
    public ISealedRestoreHooks? SealedHooks { get; set; }

    /// <summary>Where the pre-encryption snapshot lives (docs/design/step-b-spec.md 10.7): not listed, not pruned, found by name here.</summary>
    public string PreEncryptionDir => Path.Combine(_dataPath, VaultStoreFiles.PreEncryptionDir);
    /// <summary>A snapshot file by name: <c>snapshots/</c> first, then <c>pre-encryption/</c> (the restore of the way-back snapshot, spec 10.7).</summary>
    private string? FindRestorableSnapshot(string safeName)
    {
        var inSnapshots = Path.Combine(SnapshotsDir, safeName);
        if (File.Exists(inSnapshots)) return inSnapshots;
        var inPreEncryption = Path.Combine(PreEncryptionDir, safeName);
        return File.Exists(inPreEncryption) ? inPreEncryption : null;
    }

    /// <summary>
    /// An encrypted node takes no plain database from an archive (docs/design/step-b-spec.md 8.1): extracting it would write a plain image of a
    /// vault to the disk. Checked before anything is extracted, by the first bytes of every regular entry.
    /// </summary>
    private async Task RequireNoPlainDatabaseOnSealedNodeAsync(string archivePath)
    {
        if (!_connFactory.IsSealed) return;
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;

            var head = new byte[512];
            var read = await entry.DataStream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
            var prefix = head.AsSpan(0, read);
            if (read >= 16 && prefix[..16].SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidOperationException(
                    "This snapshot holds a database that is not encrypted, and this node's database is encrypted: it will not take a plain database from a file. " +
                    "Restore the snapshot on a node that is not encrypted, then encrypt that node. Nothing was changed.");
            if (LooksLikeNestedArchive(prefix))
                throw new InvalidOperationException(
                    $"This snapshot contains a nested archive in member '{entry.Name}', which cannot be verified on an encrypted node. Nothing was changed.");

            // The prefix only identifies the member. Consume every regular member completely so a
            // corrupt tail cannot be discovered by the extractor after it has already written files.
            await entry.DataStream.CopyToAsync(Stream.Null);
        }

        // TarReader stops at the end-of-archive blocks. Reading the wrapped stream to EOF validates
        // the gzip footer and prevents bytes after the tar from being hidden from this preflight.
        await gzip.CopyToAsync(Stream.Null);
    }

    private static bool LooksLikeNestedArchive(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x1F && bytes[1] == 0x8B && bytes[2] == 0x08 && (bytes[3] & 0xE0) == 0) return true;
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && ((bytes[2] == 3 && bytes[3] == 4) || (bytes[2] == 5 && bytes[3] == 6))) return true;
        if (bytes.Length >= 3 && bytes[0] == 'B' && bytes[1] == 'Z' && bytes[2] == 'h') return true;
        if (bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 })) return true;
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD })) return true;
        if (bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return true;
        if (bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 })) return true;
        return bytes.Length >= 263 && bytes[257..263].SequenceEqual("ustar\0"u8);
    }

    /// <summary>
    /// Opens the BMBDB container <paramref name="containerPath"/> in memory and leaves a sealed staging copy under the same name (docs/design/step-b-spec.md
    /// 8.4): the plain image goes through <see cref="PlainImageImporter"/> into a file encrypted with this node's page key, never to the disk as it is. The
    /// session's master DEK is tried first; when it does not open the container and the archive holds a key envelope (spec 10.7), the DEK of the
    /// envelope's slots under <paramref name="masterPassword"/> is. Every DEK and the image are cleared.
    /// </summary>
    private async Task DecryptToSealedStagingAsync(string containerPath, string? masterPassword)
    {
        if (_keys is null)
            throw new InvalidOperationException("Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");
        var blobLength = new FileInfo(containerPath).Length;
        EnsureDbSizeWithinEncryptableLimit(blobLength);

        byte[] sessionDek;
        try { sessionDek = _keys.CopyMasterDek(); }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");
        }

        byte[]? image = null;
        byte[]? envelopeDek = null;
        byte[]? pageKey = null;
        var sealing = containerPath + ".sealing";
        try
        {
            try
            {
                await using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                image = await Bmbdb2Container.DecryptAsync(stream, blobLength, sessionDek);
            }
            catch (CryptographicException ex)
            {
                var envelopePath = Path.Combine(Path.GetDirectoryName(containerPath)!, SnapshotKeyEnvelope.EntryName);
                if (masterPassword is not null && File.Exists(envelopePath))
                    envelopeDek = SnapshotKeyEnvelope.TryOpen(await File.ReadAllBytesAsync(envelopePath), masterPassword);
                if (envelopeDek is null)
                    throw new InvalidOperationException(
                        "This snapshot was made by a different vault (another master key) and cannot be opened here. Nothing was changed.", ex);
                await using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                try { image = await Bmbdb2Container.DecryptAsync(stream, blobLength, envelopeDek); }
                catch (CryptographicException inner)
                {
                    throw new InvalidOperationException(
                        "This snapshot was made by a different vault (another master key) and cannot be opened here. Nothing was changed.", inner);
                }
            }

            pageKey = _connFactory.CopyPageKey()!;
            await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image, writable: false), image.Length, sealing, pageKey, PlainImageImporter.DefaultMemoryCap());
            // In place, like every other container write of this service: a rename over the old name fails while anything has the old file open.
            await using (var source = new FileStream(sealing, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var target = new FileStream(containerPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
                await source.CopyToAsync(target);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionDek);
            if (envelopeDek is not null) CryptographicOperations.ZeroMemory(envelopeDek);
            if (image is not null) CryptographicOperations.ZeroMemory(image);
            if (pageKey is not null) CryptographicOperations.ZeroMemory(pageKey);
            if (File.Exists(sealing)) File.Delete(sealing);
        }
    }

    /// <summary>
    /// A plain node restoring a snapshot its own key does not open (the vault was reset and set up again, or its DEK rotated): the envelope of the
    /// pre-encryption snapshot (spec 10.7) opens it under the password the restore was confirmed with. Returns false when this archive has no
    /// envelope or the password opens none of its slots; the caller then reports the foreign-vault refusal.
    /// </summary>
    private async Task<bool> TryDecryptWithEnvelopeAsync(string containerPath, string? masterPassword)
    {
        var envelopePath = Path.Combine(Path.GetDirectoryName(containerPath)!, SnapshotKeyEnvelope.EntryName);
        if (masterPassword is null || !File.Exists(envelopePath)) return false;
        var dek = SnapshotKeyEnvelope.TryOpen(await File.ReadAllBytesAsync(envelopePath), masterPassword);
        if (dek is null) return false;
        try
        {
            await DecryptDbFileAsync(containerPath, dek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Before the staged database replaces the live one on an encrypted node: the master DEK its slots hand out is found, and the boot store gets the
    /// wrapper and the pending doors (<see cref="ISealedRestoreHooks.BeforeSwapAsync"/>). The returned handle runs <see cref="ISealedRestoreHooks.AfterSwapAsync"/>
    /// when it is disposed, which the caller does after the swap, on every path. Null on a plain node. Throws, before anything is replaced, when no
    /// master key that opens the staged database can be found or the node has no hooks.
    /// </summary>
    private async Task<IAsyncDisposable?> BeginSealedSwapAsync(string stagedDbPath, string? masterPassword)
    {
        if (!_connFactory.IsSealed) return null;
        var hooks = SealedHooks
            ?? throw new InvalidOperationException("This encrypted node cannot restore a snapshot here: its unlock material is not connected. Nothing was changed.");
        if (_keys is null)
            throw new InvalidOperationException("A restore needs the node's key operations.");
        var dek = _keys.FindStagedMasterDek(stagedDbPath, masterPassword, p => OpenStaged(p))
            ?? throw new InvalidOperationException(
                "This snapshot cannot be restored: the master password does not open its key slots. Nothing was changed.");
        try
        {
            using var staged = OpenStaged(stagedDbPath);
            await hooks.BeforeSwapAsync(staged, dek, CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
        return new AfterSwap(hooks);
    }

    private sealed class AfterSwap(ISealedRestoreHooks hooks) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await hooks.AfterSwapAsync(CancellationToken.None);
    }
}

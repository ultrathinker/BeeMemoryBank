using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BeeMemoryBank.Core.IO;
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

    /// <summary>
    /// A restore never produces an unencrypted node (BMB-306, the owner's rule): whatever its kind - this node only, the whole network, a blind copy,
    /// a backup folder - it lands in a node whose database is encrypted, or it is refused. The Admin page, the API and the command line all end here
    /// (<see cref="RestoreAsync"/> and the network restore ask it first, and the restore route asks it before it looks at a password, so that a refused
    /// restore has done nothing yet). There is no flag that turns this off.
    /// </summary>
    public void RequireEncryptedNodeForRestore()
    {
        if (!IsEncryptedNode) throw new RestoreRefusedException(RestoreRefusedException.PlainNodeMessage);
    }

    /// <summary>True when this node's database is encrypted: the only kind of node a restore may land in.</summary>
    public bool IsEncryptedNode => _connFactory.IsSealed;

    /// <summary>
    /// A restore takes no plain database from an archive (docs/design/step-b-spec.md 8.1; before 2.6.2 only an encrypted node refused it): extracting
    /// it would write a plain image of a vault to the disk, and a snapshot made without encryption is a backup in the open. Checked before anything is
    /// extracted, by the first bytes of every regular entry.
    /// </summary>
    private async Task RequireNoPlainDatabaseAsync(string archivePath)
    {
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
                throw new RestoreRefusedException(
                    "This snapshot holds a database that is not encrypted. A restore does not take a database in the open from a file: " +
                    "use a snapshot that this node, or one in its network, made with encryption. Nothing was changed.");
            if (LooksLikeNestedArchive(prefix))
                throw new InvalidOperationException(
                    $"This snapshot contains a nested archive in member '{entry.Name}', which cannot be verified. Nothing was changed.");

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
    /// session's master DEK opens the container; the DEK and the image are cleared.
    /// </summary>
    private async Task DecryptToSealedStagingAsync(string containerPath, CancellationToken ct = default)
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

        DecryptedImage? image = null;
        byte[]? pageKey = null;
        var sealing = containerPath + ".sealing";
        try
        {
            try
            {
                // Opened in place: the container is read into one buffer and decrypted there (BMB-304), after the memory it needs has been asked for.
                await using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                image = await Bmbdb2Container.OpenAsync(stream, blobLength, sessionDek, ct, FreeMemoryOverride);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "This snapshot was made by a different vault (another master key) and cannot be opened here. Nothing was changed.", ex);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // A cut-off or malformed container (the text of the parser is not the answer).
                throw new SnapshotContainerException(ex);
            }

            pageKey = _connFactory.CopyPageKey()!;
            // The image is imported where it lies: a legacy image (BMBDB1 from a 2.5 node, no page reserve) is deserialized in place, so the restore holds
            // one whole image, not the container's buffer and a native copy of it (BMB-304 fix round).
            try
            {
                await PlainImageImporter.ToSealedFileAsync(image, sealing, pageKey, PlainImageImporter.DefaultMemoryCap(), ct);
            }
            catch (InvalidDataException ex)
            {
                // The container authenticated, so the key is the right one and the bytes are what the maker wrote: an image that is not a SQLite database (or not one this
                // node can import) is a damaged snapshot, said with the same sentence as every other one (the parser's text is not the answer).
                throw new SnapshotContainerException(ex);
            }
            // The finished sealed copy takes the container's name in one rename: a cancellation or a failure before it leaves the container as it was, and a half-copied
            // staging file (the old copy-over-in-place, which ignored the token) can no longer be left under that name.
            ct.ThrowIfCancellationRequested();
            await OwnerOnlyFile.MoveReplaceAsync(sealing, containerPath, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionDek);
            image?.Dispose();
            if (pageKey is not null) CryptographicOperations.ZeroMemory(pageKey);
            if (File.Exists(sealing)) File.Delete(sealing);
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

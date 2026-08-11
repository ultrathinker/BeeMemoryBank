using System.Buffers.Binary;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// Persists <c>SegmentWriter.Build</c>'s raw segment bytes to disk, encrypted at rest, and loads
/// them back. This is the WP-09 piece that neither
/// <c>BeeMemoryBank.Search.Segment.SegmentWriter</c>/<c>SegmentReader</c> nor
/// <c>BeeMemoryBank.Search.Indexing.IndexBuilder</c> touch by design (see their own XML docs,
/// which say so explicitly) -- everything about disk I/O and encryption for segments lives here.
///
/// <para>
/// <b>The index key is wrapped exactly the way <c>BeeMemoryBank.Core.Embeddings.ProjectionMatrix</c>
/// wraps its own secret matrix bytes</b> -- and, more precisely, exactly the way
/// <c>ArticleService</c>/<c>MediaService</c>/<c>CommentService</c> wrap their own per-entity DEKs
/// (the more common, tested instance of this same pattern): a random 32-byte "index key" is
/// wrapped with the vault's master DEK via
/// <see cref="DekManager.WrapDek"/>/<see cref="DekManager.UnwrapDek"/> -- no bespoke AES-GCM code
/// for that step, same call shape those callers use.
/// </para>
///
/// <para>
/// <b>Deviation from the brief, found and documented during implementation:</b> segment BLOCKS
/// (~64 KiB each) are NOT wrapped via <c>DekManager.WrapDek</c>/<c>UnwrapDek</c>, even though the
/// brief calls for exactly that. <c>DekManager.UnwrapDek</c> dispatches on the wrapped blob's
/// exact byte length (48/49 bytes) to pick its unwrap path -- correct for the 32-byte secrets it
/// is tested against, but it throws <c>CryptographicException</c> for any other payload length,
/// verified empirically while building this WP. A 64 KiB block (or, e.g., a real ProjectionMatrix
/// of a few hundred KB) cannot round-trip through it. Fixing that dispatch lives in
/// <c>libs/BeeMemoryBank.Crypto/</c>, out of this WP's scope to touch. Blocks are instead
/// encrypted via <see cref="BlockCipher"/> -- see that class's doc comment for the full
/// explanation; it uses the identical AES-256-GCM primitive/sizing/framing, just called directly
/// instead of through the size-limited wrapper. The index key itself (exactly 32 bytes) still
/// goes through <c>DekManager</c> as intended. See <see cref="EncryptedSegmentFormat"/> for the
/// exact on-disk layout and the AAD binding each block to (segmentId, blockIndex).
/// </para>
///
/// <para>
/// This indirection (index key wrapped by master DEK, rather than encrypting segments with the
/// master DEK directly) matters: if the index key ever needs to change (e.g. after local
/// corruption), that's a local, node-only operation that touches neither the master DEK nor any
/// other subsystem that depends on it.
/// </para>
///
/// <para>
/// Segments and their manifest (tbl_search_index_manifest / tbl_search_index_key, both read via
/// <see cref="SegmentManifestRepository"/>) are a local cache like every other derived/cache
/// artifact in this codebase: never synced, never authoritative, always safe to discard and
/// rebuild from source article content. This class never throws an unhandled exception out of
/// <see cref="LoadAsync"/> for a segment that simply can't be read back for a known reason --
/// see <see cref="SegmentLoadResult"/>/<see cref="SegmentRebuildReason"/>. Actually triggering a
/// rebuild on that signal is a later work package's (WP-11) job.
/// </para>
/// </summary>
public sealed class EncryptedSegmentStore(
    SegmentManifestRepository manifestRepo,
    SessionService session,
    string segmentsDirectory)
{
    /// <summary>
    /// Encrypts <paramref name="segmentBytes"/> (as produced by <c>SegmentWriter.Build</c>) and
    /// writes it to disk, then records/updates its tbl_search_index_manifest row. Requires an
    /// unlocked session (throws <see cref="InvalidOperationException"/> via
    /// <see cref="SessionService.GetMasterDek"/> otherwise, same as every other master-DEK
    /// consumer in this codebase, e.g. EmbeddingProjectionService).
    /// </summary>
    public async Task StoreAsync(Guid segmentId, byte[] segmentBytes, int docCount)
    {
        ArgumentNullException.ThrowIfNull(segmentBytes);

        Directory.CreateDirectory(segmentsDirectory);

        var masterDek = session.GetMasterDek();
        byte[]? indexKey = null;
        try
        {
            int currentEpoch = await manifestRepo.GetCurrentDekEpochAsync();
            indexKey = await EnsureIndexKeyAsync(masterDek, currentEpoch);

            byte[] container = Encode(segmentId, segmentBytes, indexKey);

            string filePath = Path.Combine(segmentsDirectory, segmentId.ToString("N") + ".bmesg");
            await WriteFileAtomicAsync(filePath, container);

            await manifestRepo.UpsertManifestAsync(new SegmentManifestEntry
            {
                SegmentId = segmentId,
                FilePath = filePath,
                DocCount = docCount,
                DekEpoch = currentEpoch,
                FormatVersion = EncryptedSegmentFormat.FormatVersion,
                CreatedAt = DateTime.UtcNow,
            });
        }
        finally
        {
            Array.Clear(masterDek);
            if (indexKey != null) Array.Clear(indexKey);
        }
    }

    /// <summary>
    /// Loads a previously stored segment's decrypted bytes. Never throws for any of the known
    /// "this segment/key is not currently readable" situations -- manifest row missing, file
    /// missing, dek_epoch mismatch (master DEK rotated since encryption), wrong container format
    /// version, or a corrupted/tampered block (GCM authentication failure) -- all of those come
    /// back as <c>SegmentLoadResult.RebuildNeeded(reason)</c> instead. Still requires an unlocked
    /// session to attempt the decrypt at all (<see cref="SessionService.GetMasterDek"/> throws
    /// <see cref="InvalidOperationException"/> if locked, same as everywhere else in this
    /// codebase that needs the master DEK) -- a locked session is not one of the five documented
    /// "needs rebuild" cases, it is a normal "come back after unlocking" precondition.
    /// </summary>
    public async Task<SegmentLoadResult> LoadAsync(Guid segmentId)
    {
        var manifest = await manifestRepo.GetManifestAsync(segmentId);
        if (manifest == null)
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.ManifestMissing);

        if (manifest.FormatVersion != EncryptedSegmentFormat.FormatVersion)
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.FormatVersionMismatch);

        int currentEpoch = await manifestRepo.GetCurrentDekEpochAsync();
        if (manifest.DekEpoch != currentEpoch)
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.DekEpochMismatch);

        byte[] container;
        try
        {
            if (!File.Exists(manifest.FilePath))
                return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.FileMissing);

            container = await File.ReadAllBytesAsync(manifest.FilePath);
        }
        catch (IOException)
        {
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.FileMissing);
        }
        catch (UnauthorizedAccessException)
        {
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.FileMissing);
        }

        if (!EncryptedSegmentFormat.TryParseHeader(container, out var header))
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.CorruptedBlock);

        if (header.FormatVersion != EncryptedSegmentFormat.FormatVersion)
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.FormatVersionMismatch);

        if (header.SegmentId != segmentId)
            // The whole file was swapped for a different segment's -- same detectable-tampering
            // family as a corrupted block, just caught before ever touching the index key.
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.CorruptedBlock);

        var indexKeyRow = await manifestRepo.GetIndexKeyRowAsync();
        if (indexKeyRow == null)
            // A manifest row exists but no index key was ever recorded -- should not happen
            // (StoreAsync always writes the key before the manifest row), but if it does, it's
            // exactly the same "nothing usable to decrypt with" signal as a missing manifest.
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.ManifestMissing);

        if (indexKeyRow.DekEpoch != currentEpoch)
            return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.DekEpochMismatch);

        var masterDek = session.GetMasterDek();
        byte[]? indexKey = null;
        try
        {
            try
            {
                indexKey = DekManager.UnwrapDek(indexKeyRow.WrappedKey, indexKeyRow.IV, masterDek);
            }
            catch (CryptographicException)
            {
                // Epochs matched above, so this should not happen -- but if the row is corrupted
                // independently of epoch bookkeeping, fold it into the same DEK-related signal
                // rather than letting a CryptographicException escape uncaught.
                return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.DekEpochMismatch);
            }

            byte[] plaintext;
            try
            {
                plaintext = Decode(header, container, indexKey);
            }
            catch (CryptographicException)
            {
                return SegmentLoadResult.RebuildNeeded(SegmentRebuildReason.CorruptedBlock);
            }

            return SegmentLoadResult.Ok(plaintext);
        }
        finally
        {
            Array.Clear(masterDek);
            if (indexKey != null) Array.Clear(indexKey);
        }
    }

    /// <summary>
    /// Returns the node's current index key, generating and persisting (wrapped under
    /// <paramref name="masterDek"/>) a fresh one if none is recorded yet, or if the recorded one
    /// belongs to a stale dek_epoch (master DEK rotated since it was wrapped -- any segment
    /// encrypted under the old key is already unreadable for the same epoch-mismatch reason
    /// regardless, so there is nothing to preserve by keeping the old key around).
    /// </summary>
    private async Task<byte[]> EnsureIndexKeyAsync(byte[] masterDek, int currentEpoch)
    {
        var existing = await manifestRepo.GetIndexKeyRowAsync();
        if (existing != null && existing.DekEpoch == currentEpoch)
        {
            try
            {
                return DekManager.UnwrapDek(existing.WrappedKey, existing.IV, masterDek);
            }
            catch (CryptographicException)
            {
                // Fall through and regenerate rather than let corruption in this one row block
                // writing new segments entirely.
            }
        }

        byte[] indexKey = SecureRandom.GetBytes(CryptoConstants.KeySize);
        try
        {
            var (wrapped, iv) = DekManager.WrapDek(indexKey, masterDek);

            await manifestRepo.SaveIndexKeyRowAsync(new IndexKeyRow
            {
                WrappedKey = wrapped,
                IV = iv,
                DekEpoch = currentEpoch,
                CreatedAt = DateTime.UtcNow,
            });

            return indexKey;
        }
        catch
        {
            // Persisting the freshly generated key failed -- don't leave its plaintext bytes
            // sitting around uncleared for the caller to never receive/clear.
            Array.Clear(indexKey);
            throw;
        }
    }

    /// <summary>
    /// Splits <paramref name="plaintext"/> into fixed-size blocks and encrypts each independently
    /// with <see cref="BlockCipher.Encrypt"/> under <paramref name="indexKey"/>, using
    /// <see cref="EncryptedSegmentFormat.BuildBlockAad"/> as the AAD for its position (see
    /// <see cref="BlockCipher"/>'s doc comment for why this is <c>BlockCipher</c> rather than
    /// <c>DekManager.WrapDek</c>). Assembles the full on-disk container (header + concatenated
    /// length-prefixed blocks).
    /// </summary>
    private static byte[] Encode(Guid segmentId, byte[] plaintext, byte[] indexKey)
    {
        int blockCount = EncryptedSegmentFormat.BlockCountFor(plaintext.Length);
        var blocks = new (byte[] Iv, byte[] Wrapped)[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            int offset = i * EncryptedSegmentFormat.BlockSize;
            int length = Math.Min(EncryptedSegmentFormat.BlockSize, plaintext.Length - offset);
            byte[] blockPlaintext = new byte[length];
            Buffer.BlockCopy(plaintext, offset, blockPlaintext, 0, length);

            byte[] aad = EncryptedSegmentFormat.BuildBlockAad(segmentId, i);
            var (wrapped, iv) = BlockCipher.Encrypt(indexKey, blockPlaintext, aad);
            blocks[i] = (iv, wrapped);

            Array.Clear(blockPlaintext);
        }

        int totalSize = EncryptedSegmentFormat.HeaderSize;
        foreach (var (iv, wrapped) in blocks)
            totalSize += 4 + iv.Length + 4 + wrapped.Length;

        byte[] result = new byte[totalSize];
        EncryptedSegmentFormat.WriteHeader(result, segmentId, plaintext.Length, blockCount);

        int pos = EncryptedSegmentFormat.HeaderSize;
        foreach (var (iv, wrapped) in blocks)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos, 4), iv.Length);
            pos += 4;
            iv.CopyTo(result, pos);
            pos += iv.Length;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos, 4), wrapped.Length);
            pos += 4;
            wrapped.CopyTo(result, pos);
            pos += wrapped.Length;
        }

        return result;
    }

    /// <summary>
    /// Reverses <see cref="Encode"/>: walks the length-prefixed blocks after the header,
    /// decrypting each with <see cref="BlockCipher.Decrypt"/> under the same per-block AAD it
    /// was encrypted with. Any structural malformation (truncated length-prefixes, declared
    /// lengths overrunning the buffer, decoded bytes overrunning the header's declared original
    /// length) throws <see cref="CryptographicException"/> deliberately -- same exception family
    /// as an actual GCM authentication failure -- so <see cref="LoadAsync"/> only needs one catch
    /// clause to fold every "this container is not trustworthy" case into
    /// <see cref="SegmentRebuildReason.CorruptedBlock"/>.
    /// </summary>
    private static byte[] Decode(EncryptedSegmentFormat.ParsedHeader header, byte[] container, byte[] indexKey)
    {
        byte[] plaintext = new byte[header.OriginalLength];
        int pos = EncryptedSegmentFormat.HeaderSize;
        int written = 0;

        for (int i = 0; i < header.BlockCount; i++)
        {
            if (pos + 4 > container.Length)
                throw new CryptographicException("Truncated container: missing IV length prefix.");
            int ivLength = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(pos, 4));
            pos += 4;

            if (ivLength < 0 || pos + ivLength > container.Length)
                throw new CryptographicException("Truncated container: IV runs past end of file.");
            byte[] iv = container.AsSpan(pos, ivLength).ToArray();
            pos += ivLength;

            if (pos + 4 > container.Length)
                throw new CryptographicException("Truncated container: missing ciphertext length prefix.");
            int ctLength = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(pos, 4));
            pos += 4;

            if (ctLength < 0 || pos + ctLength > container.Length)
                throw new CryptographicException("Truncated container: ciphertext runs past end of file.");
            byte[] wrapped = container.AsSpan(pos, ctLength).ToArray();
            pos += ctLength;

            byte[] aad = EncryptedSegmentFormat.BuildBlockAad(header.SegmentId, i);
            // Throws CryptographicException on GCM auth failure (tampered ciphertext/tag, or a
            // block spliced in from a different segment/position whose AAD won't match).
            byte[] blockPlaintext = BlockCipher.Decrypt(indexKey, wrapped, iv, aad);

            if (written + blockPlaintext.Length > plaintext.Length)
                throw new CryptographicException("Decoded block overruns the header's declared segment length.");

            Buffer.BlockCopy(blockPlaintext, 0, plaintext, written, blockPlaintext.Length);
            written += blockPlaintext.Length;
            Array.Clear(blockPlaintext);
        }

        if (written != header.OriginalLength)
            throw new CryptographicException("Decoded length does not match the header's declared original length.");

        return plaintext;
    }

    private static async Task WriteFileAtomicAsync(string path, byte[] content)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
    }
}

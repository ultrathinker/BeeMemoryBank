using System.Buffers.Binary;

namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// On-disk byte layout for an encrypted segment container, and the AAD binding used for every
/// block. Pure layout/encoding logic only -- no file I/O, no crypto calls (those live in
/// <see cref="EncryptedSegmentStore"/>). Mirrors the style of
/// BeeMemoryBank.Search.Segment.SegmentLayout: every offset/size is centralized here so the
/// encoder and decoder can't silently drift apart.
///
/// <para>Container layout (all multi-byte integers little-endian):</para>
/// <code>
/// Header (32 bytes, unencrypted -- contains no secret material, only sizes/identifiers, so it
/// needs no encryption; only the block payloads below do):
///   magic          4 bytes   ASCII "BMES" ("BeeMemoryBank Encrypted Segment")
///   formatVersion  4 bytes   int32, currently 1
///   segmentId      16 bytes  Guid (Guid.TryWriteBytes / Guid(ReadOnlySpan&lt;byte&gt;) round-trip form)
///   originalLength 4 bytes   int32, length of the plaintext segment bytes (SegmentWriter.Build's
///                             output) before block-splitting
///   blockCount     4 bytes   int32, number of encrypted blocks that follow
///
/// Then, for each of blockCount blocks, back-to-back:
///   ivLength           4 bytes   int32, byte length of this block's GCM nonce (always
///                                 CryptoConstants.IvSize=12 today; stored explicitly rather than
///                                 assumed, so the format isn't silently locked to one nonce size)
///   iv                 ivLength bytes
///   ciphertextLength   4 bytes   int32, byte length of the ciphertext‖tag that follows
///   ciphertext         ciphertextLength bytes -- AES-GCM ciphertext immediately followed by its
///                                 16-byte tag (CryptoConstants.TagSize), exactly the framing
///                                 BeeMemoryBank.Crypto.AesGcmHelper uses internally. Produced/
///                                 consumed by BlockCipher.Encrypt/Decrypt in this namespace, not
///                                 DekManager -- see EncryptedSegmentStore's doc comment for why
///                                 (DekManager.WrapDek/UnwrapDek's fixed-size dispatch cannot
///                                 carry an arbitrary-length block).
/// </code>
///
/// <para>
/// Plaintext is split into fixed <see cref="BlockSize"/> (64 KiB) chunks before encryption (the
/// last block may be shorter). Each block is encrypted independently with its own random nonce
/// and AAD = <see cref="BuildBlockAad"/>, binding it to (segmentId, blockIndex). This WP only ever
/// decrypts whole segments (looping over every block); partial/range reads are a forward-looking
/// capability a later WP can add without changing this on-disk format again, because block
/// boundaries already exist.
/// </para>
/// </summary>
public static class EncryptedSegmentFormat
{
    /// <summary>The ASCII "BMES" magic bytes every encrypted segment container starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "BMES"u8;

    /// <summary>The only container format version this WP produces/understands.</summary>
    public const int FormatVersion = 1;

    /// <summary>Plaintext block size before encryption. The final block of a segment may be shorter.</summary>
    public const int BlockSize = 64 * 1024;

    // --- Header: magic(4) + formatVersion(4) + segmentId(16) + originalLength(4) + blockCount(4) ---
    public const int HeaderMagicOffset = 0;
    public const int HeaderFormatVersionOffset = 4;
    public const int HeaderSegmentIdOffset = 8;
    public const int HeaderOriginalLengthOffset = 24;
    public const int HeaderBlockCountOffset = 28;
    public const int HeaderSize = 32;

    /// <summary>Byte length of the AAD built by <see cref="BuildBlockAad"/>: 16-byte Guid + 4-byte int32.</summary>
    public const int BlockAadSize = 20;

    /// <summary>Number of <see cref="BlockSize"/>-sized (or smaller, for the last one) chunks a plaintext of the given length splits into.</summary>
    public static int BlockCountFor(int plaintextLength) =>
        plaintextLength <= 0 ? 0 : (plaintextLength + BlockSize - 1) / BlockSize;

    /// <summary>
    /// AAD binding one encrypted block to its exact position within one specific segment: 16 raw
    /// bytes of <paramref name="segmentId"/> (<c>Guid.TryWriteBytes</c>) followed by
    /// <paramref name="blockIndex"/> as a 4-byte little-endian int32 -- 20 bytes total.
    ///
    /// <para>
    /// Because AES-GCM authenticates the AAD together with the ciphertext, decrypting a block
    /// under any AAD other than the exact one it was encrypted with fails authentication. That is
    /// what makes silently splicing a block from a different segment (or reordering blocks within
    /// the same segment) detectable instead of silently decrypting into wrong-but-plausible
    /// content: an attacker with data-directory write access cannot construct a valid substitute
    /// block without the index key, because they cannot forge the matching AAD/tag pairing.
    /// </para>
    /// </summary>
    public static byte[] BuildBlockAad(Guid segmentId, int blockIndex)
    {
        var aad = new byte[BlockAadSize];
        segmentId.TryWriteBytes(aad.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(16, 4), blockIndex);
        return aad;
    }

    /// <summary>Writes the fixed 32-byte header into <paramref name="destination"/> (must be at least <see cref="HeaderSize"/> bytes).</summary>
    public static void WriteHeader(Span<byte> destination, Guid segmentId, int originalLength, int blockCount)
    {
        Magic.CopyTo(destination.Slice(HeaderMagicOffset, 4));
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(HeaderFormatVersionOffset, 4), FormatVersion);
        segmentId.TryWriteBytes(destination.Slice(HeaderSegmentIdOffset, 16));
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(HeaderOriginalLengthOffset, 4), originalLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(HeaderBlockCountOffset, 4), blockCount);
    }

    /// <summary>
    /// Parses the fixed 32-byte header. Returns false (never throws) for anything structurally
    /// wrong -- too short, or bad magic -- so the caller can fold that into the same
    /// "rebuild needed" signal as every other load failure instead of a one-off exception type.
    /// Format-version and segment-id mismatches are deliberately NOT checked here (both are
    /// legitimate, well-formed headers) -- the caller compares
    /// <see cref="ParsedHeader.FormatVersion"/> and <see cref="ParsedHeader.SegmentId"/> against
    /// what it expected and decides what that means.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out ParsedHeader header)
    {
        header = default;
        if (data.Length < HeaderSize) return false;
        if (!data.Slice(HeaderMagicOffset, 4).SequenceEqual(Magic)) return false;

        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(HeaderFormatVersionOffset, 4));
        var segmentId = new Guid(data.Slice(HeaderSegmentIdOffset, 16));
        int originalLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(HeaderOriginalLengthOffset, 4));
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(HeaderBlockCountOffset, 4));

        if (originalLength < 0 || blockCount < 0) return false;

        header = new ParsedHeader(formatVersion, segmentId, originalLength, blockCount);
        return true;
    }

    public readonly record struct ParsedHeader(int FormatVersion, Guid SegmentId, int OriginalLength, int BlockCount);
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>What a BMBCH1 header says (docs/design/step-a-spec.md 7.1). The key label names the root key, never holds it.</summary>
public sealed record ChunkedContainerHeader(int Version, string Purpose, string Key, byte[] Salt, byte[] NoncePrefix, int ChunkSize);

/// <summary>
/// HKDF-SHA256 of the root key with <paramref name="info"/> and <paramref name="salt"/>, 32 bytes; the container zeroes the key it gets.
/// A baseline's producer passes the key ring's <c>DeriveKey</c>, so the metadata key itself is never handed out; every other caller
/// uses the root-key overloads.
/// </summary>
public delegate byte[] ChunkKeyDeriver(string info, ReadOnlySpan<byte> salt);

/// <summary>
/// The chunked, authenticated container (<c>BMBCH1</c>) that wraps a baseline (root key = a metadata key) and the encrypted join
/// snapshot (root key = the master DEK). Streaming: one plaintext chunk and one ciphertext chunk in memory, whatever the size.
/// Every chunk is AES-256-GCM under a key derived from the root key and a per-file random salt, with the header hash as AAD and
/// a counter nonce whose last byte marks the final chunk, so truncation, reordering, substitution, a changed header and
/// trailing bytes all fail to open (<see cref="InvalidDataException"/>).
/// </summary>
public static class ChunkedContainer
{
    public const string PurposeBaseline = "baseline";
    public const string PurposeJoin = "join";

    public const int DefaultChunkSize = 4 * 1024 * 1024;
    public const int MinChunkSize = 4096;
    public const int MaxChunkSize = 16 * 1024 * 1024;

    private const int Version = 1;
    private const int MaxHeaderLength = 4096;
    private const int SaltLength = 16;
    private const int NoncePrefixLength = 7;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int LengthPrefixSize = sizeof(uint);
    private const string KeyDerivationInfo = "bmb-chunked-v1:";

    private static readonly byte[] Magic = "BMBCH1\0\0"u8.ToArray();

    /// <summary>The key label of a baseline: its root key is the metadata key <paramref name="keyId"/>.</summary>
    public static string MekKeyLabel(string keyId) => "mek:" + keyId;

    /// <summary>The key label of a join archive: its root key is the master DEK with this fingerprint.</summary>
    public static string DekKeyLabel(string dekFingerprint) => "dek:" + dekFingerprint;

    /// <summary>True when <paramref name="firstBytes"/> starts with the container magic (a plain SQLite file never does).</summary>
    public static bool HasMagic(ReadOnlySpan<byte> firstBytes) => firstBytes.StartsWith(Magic);

    /// <summary>The nonce of chunk <paramref name="index"/>: prefix (7), the index as uint32 big-endian (4), 1 for the final chunk else 0.</summary>
    /// <exception cref="InvalidDataException">The index does not fit 32 bits: a file of more than 2^32 chunks is refused, never wrapped.</exception>
    internal static byte[] NonceFor(byte[] prefix, long index, bool last)
    {
        if (index < 0 || index > uint.MaxValue)
            throw new InvalidDataException("The container has more chunks than the format allows.");
        var nonce = new byte[NonceLength];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength, 4), (uint)index);
        nonce[NonceLength - 1] = last ? (byte)1 : (byte)0;
        return nonce;
    }

    public static Task<ChunkedContainerHeader> EncryptAsync(
        Stream plaintext, Stream container, string purpose, string keyLabel, ReadOnlyMemory<byte> rootKey,
        int chunkSize = DefaultChunkSize, CancellationToken ct = default)
    {
        if (rootKey.Length != 32) throw new ArgumentException("The root key must be 32 bytes.", nameof(rootKey));
        return EncryptCoreAsync(plaintext, container, purpose, keyLabel, RootDeriver(rootKey.ToArray()), chunkSize, 0, ct);
    }

    /// <summary>Seals with a chunk key derived by <paramref name="derive"/> (spec 7.1: HKDF of the root key, the salt of the header, "bmb-chunked-v1:" + purpose).</summary>
    public static Task<ChunkedContainerHeader> EncryptWithAsync(
        Stream plaintext, Stream container, string purpose, string keyLabel, ChunkKeyDeriver derive,
        int chunkSize = DefaultChunkSize, CancellationToken ct = default)
        => EncryptCoreAsync(plaintext, container, purpose, keyLabel, derive, chunkSize, 0, ct);

    /// <summary>Reads the whole container, authenticating every chunk before its plaintext is written to <paramref name="plaintext"/>.</summary>
    /// <param name="resolveRootKey">Maps the header (its key label) to the 32-byte root key, or null when this node does not hold it.</param>
    /// <remarks>Plaintext of authenticated chunks is written as it goes; on any exception the caller discards the sink (a truncation is only known at the end).</remarks>
    public static Task<ChunkedContainerHeader> DecryptAsync(
        Stream container, Stream plaintext, string expectedPurpose, Func<ChunkedContainerHeader, byte[]?> resolveRootKey,
        CancellationToken ct = default)
        => DecryptCoreAsync(container, plaintext, expectedPurpose,
            h => resolveRootKey(h) is { Length: 32 } root ? RootDeriver(root) : null, 0, ct);

    /// <summary>As <see cref="DecryptAsync"/>, with the chunk key derived by the deriver <paramref name="resolve"/> returns (null: the key is not held).</summary>
    public static Task<ChunkedContainerHeader> DecryptWithAsync(
        Stream container, Stream plaintext, string expectedPurpose, Func<ChunkedContainerHeader, ChunkKeyDeriver?> resolve,
        CancellationToken ct = default)
        => DecryptCoreAsync(container, plaintext, expectedPurpose, resolve, 0, ct);

    private static ChunkKeyDeriver RootDeriver(byte[] root) => (info, salt) =>
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, root, key, salt, Encoding.UTF8.GetBytes(info));
        return key;
    };

    public static async Task<ChunkedContainerHeader> EncryptFileAsync(
        string plaintextPath, string containerPath, string purpose, string keyLabel, ReadOnlyMemory<byte> rootKey,
        int chunkSize = DefaultChunkSize, CancellationToken ct = default)
    {
        await using var input = new FileStream(plaintextPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(containerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var header = await EncryptAsync(input, output, purpose, keyLabel, rootKey, chunkSize, ct);
        await output.FlushAsync(ct);
        return header;
    }

    public static async Task<ChunkedContainerHeader> DecryptFileAsync(
        string containerPath, string plaintextPath, string expectedPurpose, Func<ChunkedContainerHeader, byte[]?> resolveRootKey,
        CancellationToken ct = default)
    {
        await using var input = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(plaintextPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var header = await DecryptAsync(input, output, expectedPurpose, resolveRootKey, ct);
        await output.FlushAsync(ct);
        return header;
    }

    /// <summary>Test hook: <paramref name="firstChunkIndex"/> starts the counter near its limit (spec 16.4 item 1).</summary>
    internal static async Task<ChunkedContainerHeader> EncryptCoreAsync(
        Stream plaintext, Stream container, string purpose, string keyLabel, ChunkKeyDeriver derive,
        int chunkSize, long firstChunkIndex, CancellationToken ct)
    {
        if (purpose is not (PurposeBaseline or PurposeJoin)) throw new ArgumentException("Unknown container purpose.", nameof(purpose));
        if (string.IsNullOrEmpty(keyLabel) || keyLabel.Length > 200) throw new ArgumentException("Bad key label.", nameof(keyLabel));
        if (chunkSize is < MinChunkSize or > MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var prefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        var header = new ChunkedContainerHeader(Version, purpose, keyLabel, salt, prefix, chunkSize);
        var headerBytes = SerializeHeader(header);
        var preamble = new byte[Magic.Length + LengthPrefixSize + headerBytes.Length];
        Magic.CopyTo(preamble, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble.AsSpan(Magic.Length, LengthPrefixSize), (uint)headerBytes.Length);
        headerBytes.CopyTo(preamble, Magic.Length + LengthPrefixSize);
        var aad = SHA256.HashData(preamble);

        var key = DeriveChunkKey(derive, salt, purpose);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            await container.WriteAsync(preamble, ct);

            var plain = new byte[chunkSize];
            var sealedChunk = new byte[LengthPrefixSize + chunkSize + TagLength];
            var peek = new byte[1];
            var filled = await ReadFullAsync(plaintext, plain, 0, ct);
            var index = firstChunkIndex;
            while (true)
            {
                var last = true;
                if (filled == chunkSize)
                {
                    last = await plaintext.ReadAsync(peek, ct) == 0;
                }

                var nonce = NonceFor(prefix, index, last);
                BinaryPrimitives.WriteUInt32LittleEndian(sealedChunk, (uint)(filled + TagLength));
                aes.Encrypt(nonce, plain.AsSpan(0, filled), sealedChunk.AsSpan(LengthPrefixSize, filled),
                    sealedChunk.AsSpan(LengthPrefixSize + filled, TagLength), aad);
                await container.WriteAsync(sealedChunk.AsMemory(0, LengthPrefixSize + filled + TagLength), ct);

                if (last) break;
                plain[0] = peek[0];
                filled = await ReadFullAsync(plaintext, plain, 1, ct);
                index++;
            }

            CryptographicOperations.ZeroMemory(plain);
            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static async Task<ChunkedContainerHeader> DecryptCoreAsync(
        Stream container, Stream plaintext, string expectedPurpose, Func<ChunkedContainerHeader, ChunkKeyDeriver?> resolve,
        long firstChunkIndex, CancellationToken ct)
    {
        var (header, aad) = await ReadHeaderAsync(container, ct);
        if (header.Purpose != expectedPurpose) throw new InvalidDataException("The container is for a different purpose.");
        var derive = resolve(header) ?? throw new InvalidDataException("The container's key is not available on this node.");

        byte[] key;
        try
        {
            key = DeriveChunkKey(derive, header.Salt, header.Purpose);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidDataException)
        {
            throw new InvalidDataException("The container's key is not available on this node.", ex);
        }
        try
        {
            using var aes = new AesGcm(key, TagLength);
            var chunkSize = header.ChunkSize;
            var cipher = new byte[chunkSize + TagLength];
            var plain = new byte[chunkSize];
            var lengthBuffer = new byte[LengthPrefixSize];

            var haveLength = await TryReadLengthAsync(container, lengthBuffer, ct)
                ?? throw new InvalidDataException("The container has no chunks.");
            var index = firstChunkIndex;
            long chunkLength = haveLength;
            while (true)
            {
                if (chunkLength < TagLength || chunkLength > chunkSize + TagLength)
                    throw new InvalidDataException("A container chunk has an impossible length.");
                await ReadExactlyOrInvalidAsync(container, cipher.AsMemory(0, (int)chunkLength), ct);

                var next = await TryReadLengthAsync(container, lengthBuffer, ct);
                var last = next is null;
                if (!last && chunkLength != chunkSize + TagLength)
                    throw new InvalidDataException("A container chunk before the last one is short.");

                var nonce = NonceFor(header.NoncePrefix, index, last);
                var plainLength = (int)chunkLength - TagLength;
                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, plainLength), cipher.AsSpan(plainLength, TagLength), plain.AsSpan(0, plainLength), aad);
                }
                catch (CryptographicException)
                {
                    throw new InvalidDataException("The container is damaged or the key is wrong.");
                }

                await plaintext.WriteAsync(plain.AsMemory(0, plainLength), ct);
                if (last) break;
                chunkLength = next!.Value;
                index++;
            }

            CryptographicOperations.ZeroMemory(plain);
            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveChunkKey(ChunkKeyDeriver derive, byte[] salt, string purpose)
    {
        var key = derive(KeyDerivationInfo + purpose, salt);
        if (key is not { Length: 32 }) throw new InvalidOperationException("The key deriver must return 32 bytes.");
        return key;
    }

    private static byte[] SerializeHeader(ChunkedContainerHeader header)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", header.Version);
            writer.WriteString("purpose", header.Purpose);
            writer.WriteString("key", header.Key);
            writer.WriteString("salt", Convert.ToBase64String(header.Salt));
            writer.WriteString("nonce_prefix", Convert.ToBase64String(header.NoncePrefix));
            writer.WriteNumber("chunk", header.ChunkSize);
            writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        if (bytes.Length > MaxHeaderLength) throw new InvalidOperationException("Container header too long.");
        return bytes;
    }

    private static async Task<(ChunkedContainerHeader Header, byte[] Aad)> ReadHeaderAsync(Stream container, CancellationToken ct)
    {
        var preamble = new byte[Magic.Length + LengthPrefixSize];
        await ReadExactlyOrInvalidAsync(container, preamble, ct);
        if (!preamble.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not a BMBCH1 container.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(preamble.AsSpan(Magic.Length));
        if (length is < 1 or > MaxHeaderLength) throw new InvalidDataException("The container header has an impossible length.");

        var all = new byte[preamble.Length + (int)length];
        preamble.CopyTo(all, 0);
        await ReadExactlyOrInvalidAsync(container, all.AsMemory(preamble.Length), ct);
        return (ParseHeader(all.AsSpan(preamble.Length)), SHA256.HashData(all));
    }

    private static ChunkedContainerHeader ParseHeader(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = false });
            using var doc = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != json.Length) throw new InvalidDataException("Trailing bytes in the container header.");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bad container header.");

            string? purpose = null, key = null, salt = null, prefix = null;
            int? version = null, chunk = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate property in the container header.");
                switch (property.Name)
                {
                    case "v": version = property.Value.GetInt32(); break;
                    case "purpose": purpose = property.Value.GetString(); break;
                    case "key": key = property.Value.GetString(); break;
                    case "salt": salt = property.Value.GetString(); break;
                    case "nonce_prefix": prefix = property.Value.GetString(); break;
                    case "chunk": chunk = property.Value.GetInt32(); break;
                    default: throw new InvalidDataException("Unknown property in the container header.");
                }
            }

            if (version != Version || purpose is null || key is null || salt is null || prefix is null || chunk is null)
                throw new InvalidDataException("Bad container header.");
            if (key.Length is 0 or > 200) throw new InvalidDataException("Bad container header.");
            var saltBytes = Convert.FromBase64String(salt);
            var prefixBytes = Convert.FromBase64String(prefix);
            if (saltBytes.Length != SaltLength || prefixBytes.Length != NoncePrefixLength)
                throw new InvalidDataException("Bad container header.");
            if (chunk.Value is < MinChunkSize or > MaxChunkSize) throw new InvalidDataException("Bad container chunk size.");
            return new ChunkedContainerHeader(Version, purpose, key, saltBytes, prefixBytes, chunk.Value);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Bad container header.", ex);
        }
    }

    /// <summary>Reads the next chunk's length, or null at a clean end of file; a partial length is a truncation.</summary>
    private static async Task<uint?> TryReadLengthAsync(Stream container, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await container.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        if (read == 0) return null;
        if (read < buffer.Length) throw new InvalidDataException("The container is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static async Task ReadExactlyOrInvalidAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The container is truncated.");
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/> until it is full or the stream ends; returns how many bytes of the buffer are filled.</summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, int offset, CancellationToken ct)
    {
        var total = offset;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

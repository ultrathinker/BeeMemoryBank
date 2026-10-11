using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>
/// What a blind host can know about a baseline container without a key (docs/design/step-a-spec.md 7.1): that it is a
/// <c>BMBCH1</c> container for a baseline under the metadata key the signed manifest names, with a well-formed header and
/// chunks that tile the file exactly. It cannot tell ciphertext from anything else a producer might sign - that is the
/// producer's authority and signature - but it does refuse every plaintext format and every damaged frame, so a database
/// copy, a ZIP or a JSON dump never becomes "the sealed baseline". The producer's own reader (Vault, track 3) holds the key and
/// decrypts; this class repeats only the framing rules that need none, so a blind program carries no Vault type.
/// </summary>
public static class BaselineContainerFraming
{
    private static readonly byte[] Magic = "BMBCH1\0\0"u8.ToArray();
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    private const int PreambleLength = 12;     // magic (8) + header length (4)
    private const int MaximumHeaderLength = 4096;
    private const int MinimumChunk = 4096;
    private const int MaximumChunk = 16 * 1024 * 1024;
    private const int TagLength = 16;
    private const int SaltLength = 16;
    private const int NoncePrefixLength = 7;
    private const long MaximumChunks = 1L << 32;

    /// <summary>Throws <see cref="InvalidDataException"/> unless the file is a well-framed baseline container for <paramref name="keyId"/>.</summary>
    public static void Validate(string containerPath, string keyId)
    {
        using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        var length = stream.Length;

        var head = new byte[SqliteHeader.Length];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (read == head.Length && head.AsSpan().SequenceEqual(SqliteHeader))
            throw new InvalidDataException("The baseline container is a plain SQLite database, not a sealed container.");
        if (read < PreambleLength || !head.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The baseline container is not a BMBCH1 container.");

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(Magic.Length));
        if (headerLength is < 1 or > MaximumHeaderLength)
            throw new InvalidDataException("The baseline container's header has an impossible length.");
        if (PreambleLength + headerLength > length)
            throw new InvalidDataException("The baseline container is truncated inside its header.");
        var header = new byte[headerLength];
        stream.Position = PreambleLength;
        stream.ReadExactly(header);
        var chunk = CheckHeader(header, keyId);

        var position = PreambleLength + (long)headerLength;
        var lengthPrefix = new byte[sizeof(uint)];
        long chunks = 0;
        while (position < length)
        {
            if (length - position < lengthPrefix.Length)
                throw new InvalidDataException("The baseline container is truncated inside a chunk length.");
            stream.Position = position;
            stream.ReadExactly(lengthPrefix);
            position += lengthPrefix.Length;
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthPrefix);
            if (chunkLength < TagLength || chunkLength > chunk + (long)TagLength)
                throw new InvalidDataException("A baseline container chunk has an impossible length.");
            if (length - position < chunkLength)
                throw new InvalidDataException("The baseline container is truncated inside a chunk.");
            position += chunkLength;
            if (++chunks > MaximumChunks)
                throw new InvalidDataException("The baseline container has more chunks than the format allows.");
            if (position < length && chunkLength != chunk + (long)TagLength)
                throw new InvalidDataException("A baseline container chunk before the last one is short.");
        }
        if (chunks == 0)
            throw new InvalidDataException("The baseline container has no chunks.");
    }

    /// <summary>The header of spec 7.1, read as strictly as its owner reads it; returns the chunk size.</summary>
    private static int CheckHeader(byte[] json, string keyId)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = false });
            using var doc = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != json.Length || doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The baseline container's header is not a single JSON object.");

            int? version = null, chunk = null;
            string? purpose = null, key = null, salt = null, prefix = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidDataException("The baseline container's header repeats a property.");
                switch (property.Name)
                {
                    case "v": version = property.Value.GetInt32(); break;
                    case "purpose": purpose = property.Value.GetString(); break;
                    case "key": key = property.Value.GetString(); break;
                    case "salt": salt = property.Value.GetString(); break;
                    case "nonce_prefix": prefix = property.Value.GetString(); break;
                    case "chunk": chunk = property.Value.GetInt32(); break;
                    default: throw new InvalidDataException("The baseline container's header has a property the format does not know.");
                }
            }
            if (version != 1 || purpose is null || key is null || salt is null || prefix is null || chunk is null)
                throw new InvalidDataException("The baseline container's header is incomplete.");
            if (purpose != "baseline")
                throw new InvalidDataException("The container is not made for a baseline.");
            if (!string.Equals(key, "mek:" + keyId, StringComparison.Ordinal))
                throw new InvalidDataException("The baseline container is sealed under another key than its manifest names.");
            if (Convert.FromBase64String(salt).Length != SaltLength || Convert.FromBase64String(prefix).Length != NoncePrefixLength)
                throw new InvalidDataException("The baseline container's header holds a salt or nonce prefix of the wrong size.");
            if (chunk.Value is < MinimumChunk or > MaximumChunk)
                throw new InvalidDataException("The baseline container's chunk size is outside what the format allows.");
            return chunk.Value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The baseline container's header cannot be read.", ex);
        }
    }
}

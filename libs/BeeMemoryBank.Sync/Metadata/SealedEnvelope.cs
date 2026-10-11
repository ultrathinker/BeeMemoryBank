using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// The payload of a protocol-4 content event (docs/design/step-a-spec.md 3.2-3.5): <c>{"v":1,"k":..,"n":..,"b":[..],"c":..}</c>.
/// Everything here is shape and needs no key, so every host checks it (a blind host before it relays, a full node before
/// it opens); sealing and opening are the Vault's (<c>MetadataEnvelope</c>).
/// </summary>
public sealed class SealedEnvelope
{
    public const int CurrentVersion = 1;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    /// <summary>The inner plaintext is padded with zero bytes to a multiple of this.</summary>
    public const int PadBlock = 64;
    public const int MaxBlobHashes = 16;
    /// <summary>The inner JSON before padding (spec 3.4); an author refuses to seal a larger one.</summary>
    public const int MaxInnerJsonBytes = 1024 * 1024;
    /// <summary>The whole payload text (spec 3.3).</summary>
    public const int MaxPayloadBytes = 3 * 512 * 1024;

    private static readonly byte[] AadDomain = "BMB-META-ENV-V1\0"u8.ToArray();

    public int Version { get; }
    public string KeyId { get; }
    public byte[] Nonce { get; }
    /// <summary>Ciphertext blob hashes the inner event needs, sorted ordinally, unique.</summary>
    public IReadOnlyList<string> BlobHashes { get; }
    /// <summary>AES-256-GCM ciphertext followed by the 16-byte tag.</summary>
    public byte[] Ciphertext { get; }

    public SealedEnvelope(string keyId, byte[] nonce, IReadOnlyList<string> blobHashes, byte[] ciphertext)
        : this(CurrentVersion, keyId, nonce, blobHashes, ciphertext)
    {
    }

    private SealedEnvelope(int version, string keyId, byte[] nonce, IReadOnlyList<string> blobHashes, byte[] ciphertext)
    {
        Version = version;
        KeyId = keyId;
        Nonce = nonce;
        BlobHashes = blobHashes;
        Ciphertext = ciphertext;
        if (Violation() is { } why) throw new InvalidDataException($"Malformed sealed envelope: {why}.");
    }

    /// <summary>The payload text: no whitespace, properties in the order v, k, n, b, c.</summary>
    public string ToPayload()
    {
        // Written by hand: every value is hex or base64, which JSON needs no escape for, and a JSON writer's default
        // encoder would escape '+'. The wire form has exactly one spelling.
        var sb = new StringBuilder(64 + Ciphertext.Length * 4 / 3 + BlobHashes.Count * 67);
        sb.Append("{\"v\":").Append(Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"k\":\"").Append(KeyId)
          .Append("\",\"n\":\"").Append(Convert.ToBase64String(Nonce))
          .Append("\",\"b\":[");
        for (var i = 0; i < BlobHashes.Count; i++)
            sb.Append(i == 0 ? "\"" : ",\"").Append(BlobHashes[i]).Append('"');
        sb.Append("],\"c\":\"").Append(Convert.ToBase64String(Ciphertext)).Append("\"}");
        return sb.ToString();
    }

    /// <summary>
    /// Reads an envelope. Any order of the five properties is accepted; a missing, duplicate or unknown property, a value
    /// of the wrong kind, a non-canonical base64 string or any rule of spec 3.3 broken is <see cref="InvalidDataException"/>.
    /// </summary>
    public static SealedEnvelope Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > MaxPayloadBytes) throw Malformed("the payload is larger than 1.5 MiB");

        int? version = null;
        string? keyId = null, nonce = null, ciphertext = null;
        List<string>? blobs = null;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Malformed("not a JSON object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw Malformed("not a JSON object");
                var name = reader.GetString()!;
                if (!seen.Add(name)) throw Malformed($"property '{name}' twice");
                if (!reader.Read()) throw Malformed("truncated");
                switch (name)
                {
                    case "v":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var v)) throw Malformed("v is not an integer");
                        version = v;
                        break;
                    case "k": keyId = ReadString(ref reader, name); break;
                    case "n": nonce = ReadString(ref reader, name); break;
                    case "c": ciphertext = ReadString(ref reader, name); break;
                    case "b":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Malformed("b is not an array");
                        blobs = [];
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.String) throw Malformed("b holds a value that is not a string");
                            blobs.Add(reader.GetString()!);
                        }
                        break;
                    default:
                        throw Malformed($"unknown property '{name}'");
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw Malformed("truncated");
            if (reader.Read()) throw Malformed("trailing data");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed sealed envelope: not valid JSON.", ex);
        }

        if (version is null || keyId is null || nonce is null || blobs is null || ciphertext is null)
            throw Malformed("a property is missing");
        if (version != CurrentVersion) throw Malformed($"envelope version {version}");
        return new SealedEnvelope(version.Value, keyId, CanonicalBase64(nonce, "n"), blobs, CanonicalBase64(ciphertext, "c"));
    }

    /// <summary>
    /// Why a received event breaks the protocol-4 wire rules (spec 3.2, the starred ones), or null. A sealed event must be
    /// protocol 4 with no article id and a well-formed envelope; a protocol-4 event of a content type is malformed (an
    /// activated author always seals content).
    /// </summary>
    public static string? WireViolation(SyncEvent evt)
    {
        if (evt.EventType == EventClasses.Sealed)
        {
            if (evt.ProtocolVersion != EventClasses.SealedProtocol) return $"a sealed event at protocol {evt.ProtocolVersion}";
            if (evt.ArticleId is not null) return "a sealed event with an article id";
            try { Parse(evt.Payload); }
            catch (InvalidDataException ex) { return ex.Message; }
            return null;
        }
        if (evt.ProtocolVersion >= EventClasses.SealedProtocol && EventClasses.IsSealable(evt.EventType))
            return $"a {evt.EventType} event in clear at protocol {evt.ProtocolVersion}";
        return null;
    }

    /// <summary>
    /// The additional data of the AEAD (spec 3.5): every clear field of the outer event and of the envelope except the
    /// nonce and the ciphertext. Integers little-endian; strings as an int32 byte length and their UTF-8 bytes.
    /// </summary>
    public static byte[] BuildAad(SyncEvent outer, SealedEnvelope envelope)
    {
        using var ms = new MemoryStream();
        ms.Write(AadDomain);
        WriteString(ms, outer.EventId.ToString("D"));
        WriteString(ms, outer.NodeId.ToString("D"));
        WriteInt64(ms, outer.LamportTs);
        WriteString(ms, outer.EventType);
        // Kind may be Unspecified after a round trip through SQLite; the same normalisation as EventSignature.
        WriteString(ms, new DateTime(outer.CreatedAt.Ticks, DateTimeKind.Utc).ToString("o"));
        WriteInt32(ms, outer.ProtocolVersion);
        WriteInt32(ms, envelope.Version);
        WriteString(ms, envelope.KeyId);
        WriteInt32(ms, envelope.BlobHashes.Count);
        foreach (var hash in envelope.BlobHashes) WriteString(ms, hash);
        return ms.ToArray();
    }

    /// <summary>The blob list of an inner event: its ciphertext hashes (spec 3.3 "b"), sorted ordinally and unique.</summary>
    public static IReadOnlyList<string> BlobListOf(SyncEvent inner) =>
        BlobReferences.Collect([inner]).OrderBy(h => h, StringComparer.Ordinal).ToList();

    private string? Violation()
    {
        if (Version != CurrentVersion) return $"envelope version {Version}";
        if (!IsKeyId(KeyId)) return "k is not 32 lowercase hex characters";
        if (Nonce is not { Length: NonceBytes }) return "n is not 12 bytes";
        if (BlobHashes.Count > MaxBlobHashes) return $"more than {MaxBlobHashes} blob hashes";
        for (var i = 0; i < BlobHashes.Count; i++)
        {
            if (!BlobReferences.IsWellFormedHash(BlobHashes[i])) return "b holds a malformed blob hash";
            if (i > 0 && string.CompareOrdinal(BlobHashes[i - 1], BlobHashes[i]) >= 0) return "b is not sorted and unique";
        }
        var length = Ciphertext.Length;
        if (length < TagBytes + PadBlock || length > TagBytes + MaxInnerJsonBytes || (length - TagBytes) % PadBlock != 0)
            return "c has a length no padded inner event has";
        return null;
    }

    /// <summary>32 lowercase hex characters: the shape of a metadata key id (spec 4.1).</summary>
    public static bool IsKeyId(string? value)
    {
        if (value is not { Length: 32 }) return false;
        foreach (var c in value)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static string ReadString(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType != JsonTokenType.String) throw Malformed($"{name} is not a string");
        return reader.GetString()!;
    }

    private static byte[] CanonicalBase64(string value, string name)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { throw Malformed($"{name} is not base64"); }
        // FromBase64String tolerates white space and loose padding bits; the wire form has exactly one spelling.
        if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
            throw Malformed($"{name} is not canonical base64");
        return bytes;
    }

    private static InvalidDataException Malformed(string why) => new($"Malformed sealed envelope: {why}.");

    private static void WriteString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteInt32(ms, bytes.Length);
        ms.Write(bytes);
    }

    private static void WriteInt32(MemoryStream ms, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteInt64(MemoryStream ms, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        ms.Write(b);
    }
}

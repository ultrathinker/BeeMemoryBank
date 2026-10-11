using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// Sealing and opening protocol-4 content events (docs/design/step-a-spec.md 3.3-3.5). The inner plaintext is
/// <c>{"t":type,"a":article id or null,"p":the protocol-3 payload}</c> padded with zero bytes to a multiple of 64, sealed
/// with AES-256-GCM under the event key of one MEK generation and a random 96-bit nonce; the AAD binds every clear field.
/// Opening fails as <see cref="InvalidDataException"/> on any change and never falls back to reading protocol 3.
/// </summary>
public static class MetadataEnvelope
{
    /// <summary>
    /// The outer event of <paramref name="inner"/> (a content event built exactly as protocol 3), unsigned: the caller signs
    /// it. Event id, node, Lamport time and creation time are the inner event's; the type is <c>sealed</c>, the protocol 4,
    /// there is no article id.
    /// </summary>
    public static SyncEvent Seal(SyncEvent inner, string keyId, byte[] eventKey)
    {
        if (!EventClasses.IsSealable(inner.EventType))
            throw new ArgumentException($"A {inner.EventType} event is not sealed; control events travel in clear.", nameof(inner));
        var json = EncodeInner(inner);
        if (json.Length > SealedEnvelope.MaxInnerJsonBytes)
            throw new InvalidOperationException($"The {inner.EventType} event is larger than a sealed envelope may hold.");

        var padded = new byte[(json.Length + SealedEnvelope.PadBlock - 1) / SealedEnvelope.PadBlock * SealedEnvelope.PadBlock];
        json.CopyTo(padded, 0);
        var outer = new SyncEvent
        {
            EventId = inner.EventId,
            NodeId = inner.NodeId,
            LamportTs = inner.LamportTs,
            CreatedAt = inner.CreatedAt,
            EventType = EventClasses.Sealed,
            ArticleId = null,
            EntityId = null,
            ProtocolVersion = EventClasses.SealedProtocol,
            Payload = "",
            Signature = [],
        };
        var blobs = SealedEnvelope.BlobListOf(inner);
        var nonce = RandomNumberGenerator.GetBytes(SealedEnvelope.NonceBytes);
        var sealedBytes = new byte[padded.Length + SealedEnvelope.TagBytes];
        try
        {
            // The AAD reads the envelope's clear fields only; a placeholder ciphertext of the right length stands in.
            var aad = SealedEnvelope.BuildAad(outer, new SealedEnvelope(keyId, nonce, blobs, sealedBytes));
            using var aes = new AesGcm(eventKey, SealedEnvelope.TagBytes);
            aes.Encrypt(nonce, padded, sealedBytes.AsSpan(0, padded.Length), sealedBytes.AsSpan(padded.Length), aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
            CryptographicOperations.ZeroMemory(json);
        }
        outer.Payload = new SealedEnvelope(keyId, nonce, blobs, sealedBytes).ToPayload();
        return outer;
    }

    /// <summary>
    /// The opened view of a verified sealed event: the outer event's ids, node, Lamport time, signature and creation time,
    /// with the inner type, article id and payload, protocol 4 and no entity id (the applier derives it).
    /// </summary>
    public static SyncEvent Open(SyncEvent outer, SealedEnvelope envelope, byte[] eventKey)
    {
        var padded = new byte[envelope.Ciphertext.Length - SealedEnvelope.TagBytes];
        try
        {
            try
            {
                using var aes = new AesGcm(eventKey, SealedEnvelope.TagBytes);
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext.AsSpan(0, padded.Length),
                    envelope.Ciphertext.AsSpan(padded.Length), padded, SealedEnvelope.BuildAad(outer, envelope));
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException($"Sealed event {outer.EventId} does not open with key {envelope.KeyId}.", ex);
            }

            var end = padded.Length;
            while (end > 0 && padded[end - 1] == 0) end--;
            if (padded.Length - end >= SealedEnvelope.PadBlock)
                throw new InvalidDataException($"Sealed event {outer.EventId}: the padding is longer than a block.");

            var view = DecodeInner(outer, padded.AsSpan(0, end));
            // The clear blob list is what a relay ships; it must be exactly what the inner event references.
            if (!SealedEnvelope.BlobListOf(view).SequenceEqual(envelope.BlobHashes, StringComparer.Ordinal))
                throw new InvalidDataException($"Sealed event {outer.EventId}: the blob list does not match the sealed payload.");
            return view;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    private static byte[] EncodeInner(SyncEvent inner)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("t", inner.EventType);
            if (inner.ArticleId is { } article) w.WriteString("a", article.ToString("D"));
            else w.WriteNull("a");
            w.WritePropertyName("p");
            // Embedded as written, so the opened view's payload is exactly the protocol-3 payload text.
            w.WriteRawValue(inner.Payload);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static SyncEvent DecodeInner(SyncEvent outer, ReadOnlySpan<byte> json)
    {
        string? type = null, payload = null;
        Guid? article = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Broken(outer, "the inner event is not an object");
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Broken(outer, $"inner property '{property.Name}' twice");
                switch (property.Name)
                {
                    case "t":
                        if (property.Value.ValueKind != JsonValueKind.String) throw Broken(outer, "t is not a string");
                        type = property.Value.GetString();
                        break;
                    case "a":
                        if (property.Value.ValueKind == JsonValueKind.Null) article = null;
                        else if (property.Value.ValueKind == JsonValueKind.String
                                 && Guid.TryParseExact(property.Value.GetString(), "D", out var a)) article = a;
                        else throw Broken(outer, "a is not an article id");
                        break;
                    case "p":
                        if (property.Value.ValueKind != JsonValueKind.Object) throw Broken(outer, "p is not an object");
                        payload = property.Value.GetRawText();
                        break;
                    default:
                        throw Broken(outer, $"unknown inner property '{property.Name}'");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Sealed event {outer.EventId}: the inner event is not valid JSON.", ex);
        }
        if (seen.Count != 3 || type is null || payload is null) throw Broken(outer, "an inner property is missing");
        if (!EventClasses.IsSealable(type)) throw Broken(outer, "the inner type is not a content type");

        return new SyncEvent
        {
            SequenceNum = outer.SequenceNum,
            EventId = outer.EventId,
            NodeId = outer.NodeId,
            LamportTs = outer.LamportTs,
            Signature = outer.Signature,
            CreatedAt = outer.CreatedAt,
            EventType = type,
            ArticleId = article,
            Payload = payload,
            ProtocolVersion = EventClasses.SealedProtocol,
            EntityId = null,
        };
    }

    private static InvalidDataException Broken(SyncEvent outer, string why) => new($"Sealed event {outer.EventId}: {why}.");
}

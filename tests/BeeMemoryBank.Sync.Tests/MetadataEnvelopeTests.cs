using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using SealedEnvelope = BeeMemoryBank.Sync.Metadata.SealedEnvelope;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 3.3-3.5, 4.2): the protocol-4 envelope and the MEK wrapping. Seal, open, the wire
/// shape, every tamper case at the cryptographic level (the applier-level cases are in the pipeline tests), nonce
/// uniqueness and the AAD layout.
/// </summary>
public class MetadataEnvelopeTests
{
    private static readonly string Hash = new('a', 64);
    private readonly string _title = MetadataCanaries.NonAscii("Title");
    private readonly string _path = "/" + MetadataCanaries.Ascii("Folder");
    private readonly byte[] _eventKey = RandomNumberGenerator.GetBytes(32);
    private readonly string _keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private SyncEvent Inner(string type = EventTypes.ArticleCreate, Guid? article = null, string? hash = null) => new()
    {
        EventId = Guid.NewGuid(),
        NodeId = Guid.NewGuid(),
        LamportTs = 42,
        EventType = type,
        ArticleId = article ?? Guid.NewGuid(),
        Payload = JsonSerializer.Serialize(new ArticleEventPayload(_title, _path, ["Tag"], null, "aXY=", "ZGVr", "aXY=", "published",
            DateTime.UtcNow, DateTime.UtcNow, CiphertextSha256: hash ?? Hash)),
        ProtocolVersion = 3,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void ASealedEvent_HasOnlyTheClearFieldsOfTheSpec_AndOpensToTheProtocol3Event()
    {
        var inner = Inner();
        var outer = MetadataEnvelope.Seal(inner, _keyId, _eventKey);

        outer.EventType.Should().Be("sealed");
        outer.ProtocolVersion.Should().Be(4);
        outer.ArticleId.Should().BeNull();
        outer.EntityId.Should().BeNull();
        (outer.EventId, outer.NodeId, outer.LamportTs, outer.CreatedAt).Should().Be((inner.EventId, inner.NodeId, inner.LamportTs, inner.CreatedAt));
        Regex.IsMatch(outer.Payload, "^\\{\"v\":1,\"k\":\"[0-9a-f]{32}\",\"n\":\"[A-Za-z0-9+/]{16}\",\"b\":\\[\"[0-9a-f]{64}\"\\],\"c\":\"[A-Za-z0-9+/=]+\"\\}$")
            .Should().BeTrue("the payload is written without whitespace, in the order v, k, n, b, c: " + outer.Payload);
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(outer.Payload), [_title, _path], "payload").Should().BeEmpty();
        SealedEnvelope.WireViolation(outer).Should().BeNull();

        var envelope = SealedEnvelope.Parse(outer.Payload);
        envelope.BlobHashes.Should().Equal(Hash);
        ((envelope.Ciphertext.Length - 16) % 64).Should().Be(0, "the inner plaintext is padded to 64 bytes");

        var view = MetadataEnvelope.Open(outer, envelope, _eventKey);
        view.EventType.Should().Be(inner.EventType);
        view.ArticleId.Should().Be(inner.ArticleId);
        view.Payload.Should().Be(inner.Payload, "handlers read the opened payload exactly as protocol 3 wrote it");
        view.ProtocolVersion.Should().Be(4);
        view.EntityId.Should().BeNull();
    }

    [Theory]
    [InlineData(EventTypes.ConceptTagRename)]
    [InlineData(EventTypes.FolderCreate)]
    public void AnEventWithoutBlobsOrArticle_SealsWithAnEmptyBlobList(string type)
    {
        var inner = Inner(type, Guid.Empty);
        inner.ArticleId = type == EventTypes.FolderCreate ? null : Guid.Empty;
        inner.Payload = "{\"old_name\":\"x\",\"new_name\":\"y\"}";
        var outer = MetadataEnvelope.Seal(inner, _keyId, _eventKey);
        var envelope = SealedEnvelope.Parse(outer.Payload);
        envelope.BlobHashes.Should().BeEmpty();
        MetadataEnvelope.Open(outer, envelope, _eventKey).ArticleId.Should().Be(inner.ArticleId);
    }

    [Fact]
    public void TheBlobsOfASealedEvent_AreReadFromTheEnvelope_WithoutOpeningIt()
    {
        // spec 3.10: a blind host fetches and ships the blobs a sealed event needs exactly as it does for protocol 3.
        var outer = MetadataEnvelope.Seal(Inner(hash: new string('d', 64)), _keyId, _eventKey);
        BlobReferences.ReferencesBlob(EventTypes.Sealed).Should().BeTrue();
        BlobReferences.Collect([outer]).Should().BeEquivalentTo([new string('d', 64)]);
        BlobReferences.Collect([MetadataEnvelope.Seal(Inner(EventTypes.FolderCreate), _keyId, _eventKey)])
            .Should().BeEmpty("a sealed event of a type without a body names no blob");
    }

    [Fact]
    public void AControlEvent_IsNeverSealed()
    {
        var inner = Inner(EventTypes.WhitelistAdd);
        FluentActions.Invoking(() => MetadataEnvelope.Seal(inner, _keyId, _eventKey)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenThousandSeals_UseTenThousandNonces_AndCiphertexts()
    {
        var inner = Inner();
        var nonces = new HashSet<string>();
        var ciphertexts = new HashSet<string>();
        for (var i = 0; i < 10_000; i++)
        {
            var envelope = SealedEnvelope.Parse(MetadataEnvelope.Seal(inner, _keyId, _eventKey).Payload);
            nonces.Add(Convert.ToBase64String(envelope.Nonce)).Should().BeTrue("a nonce is never reused");
            ciphertexts.Add(Convert.ToBase64String(envelope.Ciphertext)).Should().BeTrue("the same change sealed twice differs");
        }
    }

    public static TheoryData<string> Tampers => new()
    {
        "ciphertext byte", "tag byte", "nonce", "key id", "blob added", "blob removed", "outer type", "protocol", "event id",
        "lamport", "created at", "node", "wrong key", "padding not zero", "blob list not the payload's",
    };

    [Theory]
    [MemberData(nameof(Tampers))]
    public void EveryTamper_FailsToOpen_AndNeverReadsAsProtocol3(string tamper)
    {
        var inner = Inner();
        var outer = MetadataEnvelope.Seal(inner, _keyId, _eventKey);
        var e = SealedEnvelope.Parse(outer.Payload);
        var key = _eventKey;
        SealedEnvelope envelope = e;
        switch (tamper)
        {
            case "ciphertext byte": envelope = With(e, c: Flip(e.Ciphertext, 3)); break;
            case "tag byte": envelope = With(e, c: Flip(e.Ciphertext, e.Ciphertext.Length - 1)); break;
            case "nonce": envelope = With(e, n: Flip(e.Nonce, 0)); break;
            case "key id": envelope = With(e, k: Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))); break;
            case "blob added": envelope = With(e, b: [.. e.BlobHashes, new string('b', 64)]); break;
            case "blob removed": envelope = With(e, b: []); break;
            case "outer type": outer.EventType = EventTypes.ArticleCreate; break;
            case "protocol": outer.ProtocolVersion = 3; break;
            case "event id": outer.EventId = Guid.NewGuid(); break;
            case "lamport": outer.LamportTs++; break;
            case "created at": outer.CreatedAt = outer.CreatedAt.AddTicks(1); break;
            case "node": outer.NodeId = Guid.NewGuid(); break;
            case "wrong key": key = RandomNumberGenerator.GetBytes(32); break;
            case "padding not zero": envelope = ResealWith(outer, e, plain => plain[^1] = 1); break;
            case "blob list not the payload's":
                envelope = ResealWith(outer, e, _ => { }, blobs: [new string('c', 64)]);
                break;
        }

        FluentActions.Invoking(() => MetadataEnvelope.Open(outer, envelope, key)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void AnInnerEventOfAControlType_DoesNotOpen()
    {
        var outer = MetadataEnvelope.Seal(Inner(), _keyId, _eventKey);
        var e = SealedEnvelope.Parse(outer.Payload);
        var forged = ResealWith(outer, e, _ => { }, json: "{\"t\":\"whitelist_add\",\"a\":null,\"p\":{}}", blobs: []);
        FluentActions.Invoking(() => MetadataEnvelope.Open(outer, forged, _eventKey)).Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"C\",\"x\":1}", "unknown property")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"C\",\"k\":\"K\"}", "duplicate")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[]}", "missing")]
    [InlineData("{\"v\":2,\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"C\"}", "version")]
    [InlineData("{\"v\":\"1\",\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"C\"}", "version type")]
    [InlineData("{\"v\":1,\"k\":\"#UPPER#\",\"n\":\"N\",\"b\":[],\"c\":\"C\"}", "key id case")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"AAAAAAAAAAAAAAA=\",\"b\":[],\"c\":\"C\"}", "short nonce")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[\"#B2#\",\"#B1#\"],\"c\":\"C\"}", "unsorted blobs")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[\"#B1#\",\"#B1#\"],\"c\":\"C\"}", "duplicate blobs")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[\"zz\"],\"c\":\"C\"}", "bad blob")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"#SHORT#\"}", "ciphertext length")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"C\"} ", "trailing")]
    [InlineData("{\"v\":1,\"k\":\"K\",\"n\":\"N\",\"b\":[],\"c\":\"C\"}x", "trailing junk")]
    [InlineData("[1]", "not an object")]
    public void AMalformedEnvelope_IsRefused(string template, string why)
    {
        var good = SealedEnvelope.Parse(MetadataEnvelope.Seal(Inner(), _keyId, _eventKey).Payload);
        var json = template
            .Replace("\"K\"", "\"" + good.KeyId + "\"").Replace("#UPPER#", good.KeyId.ToUpperInvariant())
            .Replace("\"N\"", "\"" + Convert.ToBase64String(good.Nonce) + "\"")
            .Replace("\"C\"", "\"" + Convert.ToBase64String(good.Ciphertext) + "\"")
            .Replace("#SHORT#", Convert.ToBase64String(new byte[16 + 63]))
            .Replace("#B1#", new string('1', 64)).Replace("#B2#", new string('2', 64));
        if (why == "trailing")
        {
            // White space after the object is not data: accepted.
            SealedEnvelope.Parse(json).KeyId.Should().Be(good.KeyId);
            return;
        }
        FluentActions.Invoking(() => SealedEnvelope.Parse(json)).Should().Throw<InvalidDataException>(why);
    }

    [Fact]
    public void NonCanonicalBase64_AndTooManyBlobs_AndAHugePayload_AreRefused()
    {
        var good = SealedEnvelope.Parse(MetadataEnvelope.Seal(Inner(), _keyId, _eventKey).Payload);
        var payload = good.ToPayload();
        FluentActions.Invoking(() => SealedEnvelope.Parse(payload.Replace("\"n\":\"", "\"n\":\" "))).Should().Throw<InvalidDataException>();
        var many = Enumerable.Range(0, 17).Select(i => i.ToString("x2").PadLeft(64, '0')).ToList();
        FluentActions.Invoking(() => new SealedEnvelope(good.KeyId, good.Nonce, many, good.Ciphertext)).Should().Throw<InvalidDataException>();
        var huge = payload.Replace("\"b\":[", "\"b\":[\"" + new string('0', 2 * 1024 * 1024) + "\",");
        FluentActions.Invoking(() => SealedEnvelope.Parse(huge)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TheWireRules_RefuseASealedEventOfAnotherProtocol_OrWithAnArticle_AndClearContentAtProtocol4()
    {
        var outer = MetadataEnvelope.Seal(Inner(), _keyId, _eventKey);
        SealedEnvelope.WireViolation(outer).Should().BeNull();

        var p3 = Clone(outer); p3.ProtocolVersion = 3;
        SealedEnvelope.WireViolation(p3).Should().NotBeNull();
        var withArticle = Clone(outer); withArticle.ArticleId = Guid.NewGuid();
        SealedEnvelope.WireViolation(withArticle).Should().NotBeNull();
        var clear4 = Inner(); clear4.ProtocolVersion = 4;
        SealedEnvelope.WireViolation(clear4).Should().NotBeNull("an activated author always seals content");
        var control4 = Inner(EventTypes.WhitelistAdd); control4.ProtocolVersion = 4;
        SealedEnvelope.WireViolation(control4).Should().BeNull("a protocol-4 control event travels in clear");
        SealedEnvelope.WireViolation(Inner()).Should().BeNull("protocol-3 content is read as today");
    }

    [Fact]
    public void TheAad_IsTheSpecLayout()
    {
        var outer = MetadataEnvelope.Seal(Inner(), _keyId, _eventKey);
        var envelope = SealedEnvelope.Parse(outer.Payload);
        using var ms = new MemoryStream();
        ms.Write("BMB-META-ENV-V1\0"u8);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); Int(b.Length); ms.Write(b); }
        void Int(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b); }
        Str(outer.EventId.ToString("D"));
        Str(outer.NodeId.ToString("D"));
        var l = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(l, outer.LamportTs); ms.Write(l);
        Str("sealed");
        Str(new DateTime(outer.CreatedAt.Ticks, DateTimeKind.Utc).ToString("o"));
        Int(4);
        Int(1);
        Str(envelope.KeyId);
        Int(1);
        Str(Hash);
        SealedEnvelope.BuildAad(outer, envelope).Should().Equal(ms.ToArray());
    }

    // ─── MEK wrapping (spec 4.2) ────────────────────────────────────────────

    [Fact]
    public void AMek_OpensOnlyWithTheDekItNames_AndOnlyAsTheRowItWasWrittenFor()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var other = RandomNumberGenerator.GetBytes(32);
        var (mek, row) = MetadataKeyCrypto.Create(2, dek);
        row.KeyId.Should().MatchRegex("^[0-9a-f]{32}$");
        row.Generation.Should().Be(2);
        row.DekFingerprint.Should().Be(DekFingerprint.Of(dek));
        row.Wrapped.Should().HaveCount(48);
        row.Iv.Should().HaveCount(12);

        MetadataKeyCrypto.TryUnwrap(row, [other, dek]).Should().Equal(mek);
        MetadataKeyCrypto.TryUnwrap(row, [other]).Should().BeNull();
        MetadataKeyCrypto.TryUnwrap(row with { Generation = 3 }, [dek]).Should().BeNull("the generation is bound into the AAD");
        MetadataKeyCrypto.TryUnwrap(row with { KeyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) }, [dek]).Should().BeNull();
        MetadataKeyCrypto.TryUnwrap(row with { Wrapped = Flip(row.Wrapped, 0) }, [dek]).Should().BeNull();
    }

    [Fact]
    public void TheEventKey_IsHkdfOfTheMek()
    {
        var mek = RandomNumberGenerator.GetBytes(32);
        var expected = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, mek, expected, [], "bmb-meta-event-v1"u8);
        MetadataKeyCrypto.DeriveEventKey(mek).Should().Equal(expected);
        MetadataKeyCrypto.Derive(mek, "other", [1, 2]).Should().NotEqual(expected);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static byte[] Flip(byte[] bytes, int at)
    {
        var copy = (byte[])bytes.Clone();
        copy[at] ^= 0x01;
        return copy;
    }

    private static SealedEnvelope With(SealedEnvelope e, string? k = null, byte[]? n = null, IReadOnlyList<string>? b = null, byte[]? c = null) =>
        new(k ?? e.KeyId, n ?? e.Nonce, b ?? e.BlobHashes, c ?? e.Ciphertext);

    private static SyncEvent Clone(SyncEvent e) => new()
    {
        SequenceNum = e.SequenceNum, EventId = e.EventId, NodeId = e.NodeId, LamportTs = e.LamportTs, EventType = e.EventType,
        ArticleId = e.ArticleId, EntityId = e.EntityId, Payload = e.Payload, Signature = e.Signature, ProtocolVersion = e.ProtocolVersion,
        CreatedAt = e.CreatedAt,
    };

    /// <summary>What a key holder could craft: the decrypted plaintext changed (or replaced) and sealed again correctly.</summary>
    private SealedEnvelope ResealWith(SyncEvent outer, SealedEnvelope e, Action<byte[]> change, string? json = null, IReadOnlyList<string>? blobs = null)
    {
        byte[] plain;
        if (json is null)
        {
            plain = new byte[e.Ciphertext.Length - 16];
            using var dec = new AesGcm(_eventKey, 16);
            dec.Decrypt(e.Nonce, e.Ciphertext.AsSpan(0, plain.Length), e.Ciphertext.AsSpan(plain.Length), plain, SealedEnvelope.BuildAad(outer, e));
        }
        else
        {
            var raw = Encoding.UTF8.GetBytes(json);
            plain = new byte[(raw.Length + 63) / 64 * 64];
            raw.CopyTo(plain, 0);
        }
        change(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var sealedBytes = new byte[plain.Length + 16];
        var target = new SealedEnvelope(e.KeyId, nonce, blobs ?? e.BlobHashes, sealedBytes);
        using var enc = new AesGcm(_eventKey, 16);
        enc.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), SealedEnvelope.BuildAad(outer, target));
        return new SealedEnvelope(e.KeyId, nonce, blobs ?? e.BlobHashes, sealedBytes);
    }
}

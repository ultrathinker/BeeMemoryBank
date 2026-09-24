using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// Pins <see cref="EnvelopeFraming"/> to the exact bytes every existing article/media row was
/// written with.
///
/// <para>
/// Three independent references are used, so a drift in any one place is caught:
/// <list type="number">
///   <item>Fixed golden vectors produced OUTSIDE this codebase (Python <c>cryptography</c> AESGCM,
///   <c>uuid.bytes_le</c> for <see cref="Guid.ToByteArray"/>), for v0 and v1, article and media.</item>
///   <item><see cref="Legacy"/>: a verbatim copy of the inline framing logic the service call sites
///   used before the helper existed (the <c>isV1 = len &gt; 48 &amp;&amp; blob[0] == 0x01</c> rule, the
///   <c>"bmb-art-dek" || id</c> style AAD concatenation). New writes must open with it and old
///   writes must open with the helper.</item>
///   <item>A raw <see cref="AesGcm"/> reader that knows only the documented format, not the
///   codebase's helpers.</item>
/// </list>
/// </para>
/// </summary>
public class EnvelopeFramingTests
{
    // ---- Golden vectors (generated with Python cryptography 45 AESGCM; see class summary) ----

    private static readonly byte[] GoldenMaster = Enumerable.Range(0x00, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] GoldenEntityDek = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] GoldenDekIv = Convert.FromHexString("000102030405060708090a0b");
    private static readonly byte[] GoldenBodyIv = Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaab");
    private static readonly Guid GoldenArticleId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    private static readonly Guid GoldenMediaId = Guid.Parse("9b2e7c41-05d6-4a8f-b3e1-7d0c2a55e6f9");
    private const string GoldenText = "Golden \u0416 vector";
    private static readonly byte[] GoldenMedia = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];

    private const string ArticleV1WrappedDek = "016723f438e1c0e43ca568bda09dc45642b3e7b507c44e694b005edfbe21543e8dfcdb1f542e5acb8eb99f1aac7734ced3";
    private const string ArticleV1Body = "3953c850a1b9a07a3708dbdf452919d401c48c0cf5f7169842e959c906be10b6";
    private const string ArticleV0WrappedDek = "6723f438e1c0e43ca568bda09dc45642b3e7b507c44e694b005edfbe21543e8d74943e2a269bf4fe0b89d0e48c7cdd01";
    private const string ArticleV0Body = "3953c850a1b9a07a3708dbdf452919d46e6036961d24c6b3233a361838f4d276";
    private const string MediaV1WrappedDek = "016723f438e1c0e43ca568bda09dc45642b3e7b507c44e694b005edfbe21543e8dadc35f6813ddaca2c428518001746da3";
    private const string MediaV1Body = "f76cea73c9dd9aa0a02aae8540e2af277c75e8e1967466e357bffd";
    private const string MediaV0WrappedDek = "6723f438e1c0e43ca568bda09dc45642b3e7b507c44e694b005edfbe21543e8d74943e2a269bf4fe0b89d0e48c7cdd01";
    private const string MediaV0Body = "f76cea73c9dd9aa0a02aae51991b0b843b15fd106a620e52a0c849";

    private const string ArticleDekAadHex = "626d622d6172742d64656be004253f894fd3419a0c0305e82c3301";
    private const string ArticleBodyAadHex = "626d622d6172742d626f6479e004253f894fd3419a0c0305e82c3301";
    private const string MediaDekAadHex = "626d622d6d656469612d64656b417c2e9bd6058f4ab3e17d0c2a55e6f9";
    private const string MediaBodyAadHex = "626d622d6d65646961417c2e9bd6058f4ab3e17d0c2a55e6f9";

    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static byte[] Key() => SecureRandom.GetBytes(32);

    /// <summary>
    /// The inline logic every call site carried before <see cref="EnvelopeFraming"/> — kept here
    /// verbatim as the compatibility reference. Do not "tidy" it to call the helper.
    /// </summary>
    private static class Legacy
    {
        public static (byte[] ct, byte[] iv, byte[] dek, byte[] dekIv) WriteArticle(Guid articleId, string plaintext, byte[] masterDek)
        {
            var articleDek = DekManager.GenerateArticleDek();
            try
            {
                var dekAad = "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();
                var bodyAad = "bmb-art-body"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();
                var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, articleDek, bodyAad);
                var (encryptedDek, dekIv) = DekManager.WrapDek(articleDek, masterDek, dekAad);
                return (ciphertext, iv, encryptedDek, dekIv);
            }
            finally
            {
                Array.Clear(articleDek);
            }
        }

        public static string ReadArticle(Guid id, byte[] ct, byte[] iv, byte[] encryptedDek, byte[] dekIv, byte[] masterDek)
        {
            var isV1 = encryptedDek.Length > 48 && encryptedDek[0] == 0x01;
            var dekAad = isV1 ? "bmb-art-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
            var bodyAad = isV1 ? "bmb-art-body"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
            var articleDek = DekManager.UnwrapDek(encryptedDek, dekIv, masterDek, dekAad);
            try
            {
                return ArticleEncryptor.Decrypt(ct, iv, articleDek, bodyAad);
            }
            finally
            {
                Array.Clear(articleDek);
            }
        }

        public static (byte[] ct, byte[] iv, byte[] dek, byte[] dekIv) WriteMedia(Guid mediaId, byte[] plaintext, byte[] masterDek)
        {
            var mediaDek = DekManager.GenerateArticleDek();
            try
            {
                var dekAad = "bmb-media-dek"u8.ToArray().Concat(mediaId.ToByteArray()).ToArray();
                var bodyAad = "bmb-media"u8.ToArray().Concat(mediaId.ToByteArray()).ToArray();
                var (ciphertext, iv) = MediaEncryptor.Encrypt(plaintext, mediaDek, bodyAad);
                var (encryptedDek, dekIv) = DekManager.WrapDek(mediaDek, masterDek, dekAad);
                return (ciphertext, iv, encryptedDek, dekIv);
            }
            finally
            {
                Array.Clear(mediaDek);
            }
        }

        public static byte[] ReadMedia(Guid id, byte[] ct, byte[] iv, byte[] encryptedDek, byte[] dekIv, byte[] masterDek)
        {
            var isV1 = encryptedDek.Length > 48 && encryptedDek[0] == 0x01;
            var dekAad = isV1 ? "bmb-media-dek"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
            var mediaDek = DekManager.UnwrapDek(encryptedDek, dekIv, masterDek, dekAad);
            try
            {
                var bodyAad = isV1 ? "bmb-media"u8.ToArray().Concat(id.ToByteArray()).ToArray() : null;
                return MediaEncryptor.Decrypt(ct, iv, mediaDek, bodyAad);
            }
            finally
            {
                Array.Clear(mediaDek);
            }
        }

        /// <summary>A row as a pre-v1 build left it: 48-byte DEK wrap, no AAD anywhere.</summary>
        public static (byte[] ct, byte[] iv, byte[] dek, byte[] dekIv) WriteV0(byte[] plaintext, byte[] masterDek)
        {
            var entityDek = DekManager.GenerateArticleDek();
            try
            {
                var (ciphertext, iv) = MediaEncryptor.Encrypt(plaintext, entityDek, aad: null);
                var (encryptedDek, dekIv) = DekManager.WrapDekLegacyV0(entityDek, masterDek);
                return (ciphertext, iv, encryptedDek, dekIv);
            }
            finally
            {
                Array.Clear(entityDek);
            }
        }
    }

    /// <summary>Opens a row with nothing but <see cref="AesGcm"/> and the documented format.</summary>
    private static byte[] RawOpen(byte[] key, byte[] ciphertextWithTag, byte[] iv, byte[]? aad)
    {
        var ctLen = ciphertextWithTag.Length - 16;
        var plain = new byte[ctLen];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(iv, ciphertextWithTag.AsSpan(0, ctLen), ciphertextWithTag.AsSpan(ctLen), plain, aad);
        return plain;
    }

    // ---- AAD bytes and the dispatch rule ----

    [Fact]
    public void Aads_AreTheExactHistoricalBytes()
    {
        EnvelopeFraming.Article.DekAad(GoldenArticleId).Should().Equal(Hex(ArticleDekAadHex));
        EnvelopeFraming.Article.BodyAad(GoldenArticleId).Should().Equal(Hex(ArticleBodyAadHex));
        EnvelopeFraming.Media.DekAad(GoldenMediaId).Should().Equal(Hex(MediaDekAadHex));
        EnvelopeFraming.Media.BodyAad(GoldenMediaId).Should().Equal(Hex(MediaBodyAadHex));
    }

    [Theory]
    [InlineData(48, 0x01, false)] // legacy v0: exactly 48 bytes, whatever the first byte is
    [InlineData(48, 0x00, false)]
    [InlineData(49, 0x01, true)]  // v1
    [InlineData(49, 0x00, false)] // malformed: not v1 (UnwrapDek rejects it later)
    [InlineData(50, 0x01, true)]  // malformed: the historical rule still calls it v1
    [InlineData(47, 0x01, false)]
    [InlineData(0, 0x00, false)]
    public void IsVersioned_MatchesTheHistoricalRule(int length, byte first, bool expected)
    {
        var blob = new byte[length];
        if (length > 0) blob[0] = first;

        var historical = blob.Length > 48 && blob[0] == 0x01;
        historical.Should().Be(expected, "the test data must describe the historical rule");
        EnvelopeFraming.IsVersioned(blob).Should().Be(expected);
    }

    // ---- Golden vectors → helper (existing rows stay readable) ----

    [Theory]
    [InlineData(ArticleV1WrappedDek, ArticleV1Body)]
    [InlineData(ArticleV0WrappedDek, ArticleV0Body)]
    public void Article_GoldenRows_DecryptWithHelper(string wrappedHex, string bodyHex)
    {
        var wrapped = Hex(wrappedHex);
        var dek = EnvelopeFraming.Article.UnwrapDek(GoldenArticleId, wrapped, GoldenDekIv, GoldenMaster);
        dek.Should().Equal(GoldenEntityDek);

        EnvelopeFraming.Article.DecryptBodyText(GoldenArticleId, wrapped, dek, Hex(bodyHex), GoldenBodyIv)
            .Should().Be(GoldenText);
        // And the legacy inline reader agrees, so the vectors really are the historical format.
        Legacy.ReadArticle(GoldenArticleId, Hex(bodyHex), GoldenBodyIv, wrapped, GoldenDekIv, GoldenMaster)
            .Should().Be(GoldenText);
    }

    [Theory]
    [InlineData(MediaV1WrappedDek, MediaV1Body)]
    [InlineData(MediaV0WrappedDek, MediaV0Body)]
    public void Media_GoldenRows_DecryptWithHelper(string wrappedHex, string bodyHex)
    {
        var wrapped = Hex(wrappedHex);
        var dek = EnvelopeFraming.Media.UnwrapDek(GoldenMediaId, wrapped, GoldenDekIv, GoldenMaster);
        dek.Should().Equal(GoldenEntityDek);

        EnvelopeFraming.Media.DecryptBody(GoldenMediaId, wrapped, dek, Hex(bodyHex), GoldenBodyIv)
            .Should().Equal(GoldenMedia);
        Legacy.ReadMedia(GoldenMediaId, Hex(bodyHex), GoldenBodyIv, wrapped, GoldenDekIv, GoldenMaster)
            .Should().Equal(GoldenMedia);
    }

    [Fact]
    public void GoldenV1Rows_AreNotInterchangeableBetweenArticleAndMedia()
    {
        // Same keys and IVs, different purpose tag: the article framing must not open a media row.
        var act = () => EnvelopeFraming.Article.UnwrapDek(GoldenMediaId, Hex(MediaV1WrappedDek), GoldenDekIv, GoldenMaster);
        act.Should().Throw<CryptographicException>();
    }

    // ---- Legacy writer → helper reader ----

    [Fact]
    public void Article_LegacyV1Write_ReadsWithHelper()
    {
        var id = Guid.NewGuid();
        var master = Key();
        var (ct, iv, dek, dekIv) = Legacy.WriteArticle(id, "legacy v1 \u00e9", master);

        var entityDek = EnvelopeFraming.Article.UnwrapDek(id, dek, dekIv, master);
        EnvelopeFraming.Article.DecryptBodyText(id, dek, entityDek, ct, iv).Should().Be("legacy v1 \u00e9");
    }

    [Fact]
    public void Article_LegacyV0Write_ReadsWithHelper()
    {
        var master = Key();
        var (ct, iv, dek, dekIv) = Legacy.WriteV0(Encoding.UTF8.GetBytes("legacy v0"), master);

        var id = Guid.NewGuid(); // v0 AAD does not depend on the id at all
        var entityDek = EnvelopeFraming.Article.UnwrapDek(id, dek, dekIv, master);
        EnvelopeFraming.Article.DecryptBodyText(id, dek, entityDek, ct, iv).Should().Be("legacy v0");
    }

    [Fact]
    public void Media_LegacyV1Write_ReadsWithHelper()
    {
        var id = Guid.NewGuid();
        var master = Key();
        byte[] payload = [1, 2, 3, 250, 251];
        var (ct, iv, dek, dekIv) = Legacy.WriteMedia(id, payload, master);

        var entityDek = EnvelopeFraming.Media.UnwrapDek(id, dek, dekIv, master);
        EnvelopeFraming.Media.DecryptBody(id, dek, entityDek, ct, iv).Should().Equal(payload);
    }

    [Fact]
    public void Media_LegacyV0Write_ReadsWithHelper()
    {
        var master = Key();
        byte[] payload = [9, 8, 7];
        var (ct, iv, dek, dekIv) = Legacy.WriteV0(payload, master);

        var id = Guid.NewGuid();
        var entityDek = EnvelopeFraming.Media.UnwrapDek(id, dek, dekIv, master);
        EnvelopeFraming.Media.DecryptBody(id, dek, entityDek, ct, iv).Should().Equal(payload);
    }

    // ---- Helper writer → legacy reader and raw reader ----

    [Fact]
    public void Article_HelperWrite_IsV1_AndReadsWithLegacyAndRawReaders()
    {
        var id = Guid.NewGuid();
        var master = Key();
        var sealedRow = EnvelopeFraming.Article.SealNewText(id, "new \u0416 row", master);

        sealedRow.WrappedDek.Length.Should().Be(49);
        sealedRow.WrappedDek[0].Should().Be(0x01);

        Legacy.ReadArticle(id, sealedRow.Ciphertext, sealedRow.Iv, sealedRow.WrappedDek, sealedRow.DekIv, master)
            .Should().Be("new \u0416 row");

        var dek = RawOpen(master, sealedRow.WrappedDek[1..], sealedRow.DekIv,
            Encoding.ASCII.GetBytes("bmb-art-dek").Concat(id.ToByteArray()).ToArray());
        Encoding.UTF8.GetString(RawOpen(dek, sealedRow.Ciphertext, sealedRow.Iv,
            Encoding.ASCII.GetBytes("bmb-art-body").Concat(id.ToByteArray()).ToArray()))
            .Should().Be("new \u0416 row");
    }

    [Fact]
    public void Media_HelperWrite_IsV1_AndReadsWithLegacyAndRawReaders()
    {
        var id = Guid.NewGuid();
        var master = Key();
        byte[] payload = [0xff, 0xd8, 0xff, 0x00, 0x42];
        var sealedRow = EnvelopeFraming.Media.SealNew(id, payload, master);

        sealedRow.WrappedDek.Length.Should().Be(49);
        Legacy.ReadMedia(id, sealedRow.Ciphertext, sealedRow.Iv, sealedRow.WrappedDek, sealedRow.DekIv, master)
            .Should().Equal(payload);

        var dek = RawOpen(master, sealedRow.WrappedDek[1..], sealedRow.DekIv,
            Encoding.ASCII.GetBytes("bmb-media-dek").Concat(id.ToByteArray()).ToArray());
        RawOpen(dek, sealedRow.Ciphertext, sealedRow.Iv,
            Encoding.ASCII.GetBytes("bmb-media").Concat(id.ToByteArray()).ToArray())
            .Should().Equal(payload);
    }

    [Fact]
    public void Article_SealWithExistingDek_UpgradesAV0RowToV1Consistently()
    {
        // Updating a body re-seals BOTH layers, so a v0 row becomes a coherent v1 row.
        var id = Guid.NewGuid();
        var master = Key();
        var (_, _, v0Dek, v0DekIv) = Legacy.WriteV0(Encoding.UTF8.GetBytes("old"), master);

        var entityDek = EnvelopeFraming.Article.UnwrapDek(id, v0Dek, v0DekIv, master);
        var resealed = EnvelopeFraming.Article.SealText(id, "new", entityDek, master);

        EnvelopeFraming.IsVersioned(resealed.WrappedDek).Should().BeTrue();
        Legacy.ReadArticle(id, resealed.Ciphertext, resealed.Iv, resealed.WrappedDek, resealed.DekIv, master)
            .Should().Be("new");
        EnvelopeFraming.Article.UnwrapDek(id, resealed.WrappedDek, resealed.DekIv, master).Should().Equal(entityDek);
    }

    // ---- Binding: a v1 row cannot be opened under a different id ----

    [Fact]
    public void V1Row_DoesNotOpenUnderAnotherId()
    {
        var id = Guid.NewGuid();
        var master = Key();
        var sealedRow = EnvelopeFraming.Article.SealNewText(id, "bound", master);

        var unwrapElsewhere = () => EnvelopeFraming.Article.UnwrapDek(Guid.NewGuid(), sealedRow.WrappedDek, sealedRow.DekIv, master);
        unwrapElsewhere.Should().Throw<CryptographicException>();

        var dek = EnvelopeFraming.Article.UnwrapDek(id, sealedRow.WrappedDek, sealedRow.DekIv, master);
        var decryptElsewhere = () => EnvelopeFraming.Article.DecryptBodyText(Guid.NewGuid(), sealedRow.WrappedDek, dek, sealedRow.Ciphertext, sealedRow.Iv);
        decryptElsewhere.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void UnwrapDek_WithTheWrongMasterKey_ThrowsCryptographicException()
    {
        // Candidate-DEK walks (SessionService.TryUnwrapWithCandidates) rely on exactly this type.
        var id = Guid.NewGuid();
        var sealedRow = EnvelopeFraming.Media.SealNew(id, [1, 2, 3], Key());

        var act = () => EnvelopeFraming.Media.UnwrapDek(id, sealedRow.WrappedDek, sealedRow.DekIv, Key());
        act.Should().Throw<CryptographicException>();
    }

    // ---- Rewrap (DEK rotation) preserves framing ----

    [Fact]
    public void RewrapDek_KeepsAV0RowV0_AndTheLegacyReaderStillOpensIt()
    {
        var oldMaster = Key();
        var newMaster = Key();
        var id = Guid.NewGuid();
        var (ct, iv, v0Dek, v0DekIv) = Legacy.WriteV0(Encoding.UTF8.GetBytes("v0 body"), oldMaster);

        var plainDek = EnvelopeFraming.Article.UnwrapDek(id, v0Dek, v0DekIv, oldMaster);
        var (rewrapped, rewrappedIv) = EnvelopeFraming.Article.RewrapDek(id, plainDek, v0Dek, newMaster);

        rewrapped.Length.Should().Be(48, "a v0 row must still look v0 to every reader");
        Legacy.ReadArticle(id, ct, iv, rewrapped, rewrappedIv, newMaster).Should().Be("v0 body");
    }

    [Fact]
    public void RewrapDek_KeepsAV1RowV1_AndTheLegacyReaderStillOpensIt()
    {
        var oldMaster = Key();
        var newMaster = Key();
        var id = Guid.NewGuid();
        var (ct, iv, v1Dek, v1DekIv) = Legacy.WriteMedia(id, [4, 5, 6], oldMaster);

        var plainDek = EnvelopeFraming.Media.UnwrapDek(id, v1Dek, v1DekIv, oldMaster);
        var (rewrapped, rewrappedIv) = EnvelopeFraming.Media.RewrapDek(id, plainDek, v1Dek, newMaster);

        rewrapped.Length.Should().Be(49);
        Legacy.ReadMedia(id, ct, iv, rewrapped, rewrappedIv, newMaster).Should().Equal([4, 5, 6]);
    }

    [Fact]
    public void RewrapDek_GoldenV0Row_StaysReadable()
    {
        var newMaster = Key();
        var wrapped = Hex(ArticleV0WrappedDek);
        var plainDek = EnvelopeFraming.Article.UnwrapDek(GoldenArticleId, wrapped, GoldenDekIv, GoldenMaster);

        var (rewrapped, rewrappedIv) = EnvelopeFraming.Article.RewrapDek(GoldenArticleId, plainDek, wrapped, newMaster);

        Legacy.ReadArticle(GoldenArticleId, Hex(ArticleV0Body), GoldenBodyIv, rewrapped, rewrappedIv, newMaster)
            .Should().Be(GoldenText);
    }
}

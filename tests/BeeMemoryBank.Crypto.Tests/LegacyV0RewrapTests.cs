using System.Security.Cryptography;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// Pins the framing contract DEK rotation depends on.
///
/// <para>
/// Every reader decides whether a row is v1 by inspecting the DEK blob itself
/// (<c>length &gt; 48 &amp;&amp; blob[0] == 0x01</c>) and then applies the v1 AAD to BOTH the DEK
/// unwrap AND the body decrypt. Rotation therefore cannot upgrade a legacy v0 row's framing while
/// re-wrapping it: the reader would flip to v1 AAD against a body ciphertext that is still v0 and
/// was sealed with none, and the article would be permanently undecryptable — a silent, total loss
/// of that row's content, discovered only when someone opened it.
/// </para>
/// </summary>
public class LegacyV0RewrapTests
{
    private static byte[] Key() => SecureRandom.GetBytes(32);

    [Fact]
    public void WrapDekLegacyV0_ProducesTheLegacyFraming()
    {
        var dek = DekManager.GenerateArticleDek();
        var master = Key();

        var (wrapped, _) = DekManager.WrapDekLegacyV0(dek, master);

        wrapped.Length.Should().Be(48, "v0 has no version byte");
        // The v1 detector every reader uses must NOT fire on this.
        (wrapped.Length > 48 && wrapped[0] == 0x01).Should().BeFalse();
    }

    [Fact]
    public void AV0RowRewrappedAsV0_StillUnwrapsWithNoAad()
    {
        var dek = DekManager.GenerateArticleDek();
        var oldMaster = Key();
        var newMaster = Key();

        // A row as an old build left it: no version byte, no AAD.
        var (v0Wrapped, v0Iv) = DekManager.WrapDekLegacyV0(dek, oldMaster);
        v0Wrapped.Length.Should().Be(48);

        // Rotation: unwrap under the old master, re-wrap under the new one, framing preserved.
        var plain = DekManager.UnwrapDek(v0Wrapped, v0Iv, oldMaster, aad: null);
        var (rewrapped, newIv) = DekManager.WrapDekLegacyV0(plain, newMaster);

        rewrapped.Length.Should().Be(48, "the row must still look v0 to every reader");
        DekManager.UnwrapDek(rewrapped, newIv, newMaster, aad: null).Should().Equal(dek);
    }

    /// <summary>
    /// The regression itself: re-wrapping a v0 row with WrapDek (which always emits v1) relabels it,
    /// and the reader's v1 path then fails. This is what rotation used to do to every legacy row.
    /// </summary>
    [Fact]
    public void AV0RowRewrappedAsV1_BecomesUnreadableToTheReaderPath()
    {
        var articleId = Guid.NewGuid();
        var dek = DekManager.GenerateArticleDek();
        var oldMaster = Key();
        var newMaster = Key();

        var (v0Wrapped, v0Iv) = DekManager.WrapDekLegacyV0(dek, oldMaster);
        var plain = DekManager.UnwrapDek(v0Wrapped, v0Iv, oldMaster, aad: null);

        // The old, wrong rotation behavior: WrapDek always emits v1, and aad was null for a v0 row.
        var (upgraded, upgradedIv) = DekManager.WrapDek(plain, newMaster, aad: null);
        upgraded.Length.Should().Be(49);

        // A reader now sees v1 framing and supplies the v1 AAD, which was never used to seal it.
        var isV1 = upgraded.Length > 48 && upgraded[0] == 0x01;
        isV1.Should().BeTrue("this is precisely the misdetection");
        var readerAad = "bmb-art-dek"u8.ToArray().Concat(articleId.ToByteArray()).ToArray();

        var act = () => DekManager.UnwrapDek(upgraded, upgradedIv, newMaster, readerAad);
        act.Should().Throw<CryptographicException>("the row is lost — this is the behavior being prevented");
    }
}

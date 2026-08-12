using BeeMemoryBank.Crypto;
using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto.Tests;

public class DekManagerTests
{
    [Fact]
    public void WrapUnwrap_Roundtrip()
    {
        var articleDek = DekManager.GenerateArticleDek();
        var masterDek = MasterKeyManager.GenerateMasterDek();

        var (wrapped, iv) = DekManager.WrapDek(articleDek, masterDek);
        var unwrapped = DekManager.UnwrapDek(wrapped, iv, masterDek);

        unwrapped.Should().Equal(articleDek);
    }

    [Fact]
    public void WrapUnwrap_WrongMasterDek_Throws()
    {
        var articleDek = DekManager.GenerateArticleDek();
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var wrongMasterDek = MasterKeyManager.GenerateMasterDek();

        var (wrapped, iv) = DekManager.WrapDek(articleDek, masterDek);

        var act = () => DekManager.UnwrapDek(wrapped, iv, wrongMasterDek);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void GenerateArticleDek_Is32Bytes()
    {
        var dek = DekManager.GenerateArticleDek();
        dek.Should().HaveCount(CryptoConstants.KeySize);
    }

    // UnwrapVersioned: added alongside UnwrapDek for payloads that are not a fixed 32-byte secret
    // (e.g. ProjectionMatrix's ~590 KB matrix), which UnwrapDek's length-exact v0/v1 dispatch
    // cannot carry. See DekManager.UnwrapVersioned's doc comment for the full rationale.

    [Fact]
    public void WrapUnwrapVersioned_ArbitraryLargePayload_Roundtrips()
    {
        var payload = new byte[600_000];
        Random.Shared.NextBytes(payload);
        var masterDek = MasterKeyManager.GenerateMasterDek();

        var (wrapped, iv) = DekManager.WrapDek(payload, masterDek);
        var unwrapped = DekManager.UnwrapVersioned(wrapped, iv, masterDek);

        unwrapped.Should().Equal(payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(47)]
    public void WrapUnwrapVersioned_OddLengthPayloads_Roundtrip(int length)
    {
        // Deliberately includes lengths that collide with, or sit right next to, UnwrapDek's
        // fixed v0 (48) / v1 (49) total-length thresholds -- UnwrapVersioned must not care.
        var payload = new byte[length];
        Random.Shared.NextBytes(payload);
        var masterDek = MasterKeyManager.GenerateMasterDek();

        var (wrapped, iv) = DekManager.WrapDek(payload, masterDek);
        var unwrapped = DekManager.UnwrapVersioned(wrapped, iv, masterDek);

        unwrapped.Should().Equal(payload);
    }

    [Fact]
    public void UnwrapVersioned_WrongMasterDek_Throws()
    {
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var wrongMasterDek = MasterKeyManager.GenerateMasterDek();

        var (wrapped, iv) = DekManager.WrapDek(payload, masterDek);

        var act = () => DekManager.UnwrapVersioned(wrapped, iv, wrongMasterDek);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void UnwrapVersioned_MissingVersionByte_ThrowsRatherThanSilentlyReinterpreting()
    {
        // A hand-crafted blob whose first byte is not Version1 (0x01) must be rejected outright --
        // there is no v0/legacy fallback in UnwrapVersioned, unlike UnwrapDek.
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var bogus = new byte[64];
        bogus[0] = 0x00;
        var iv = SecureRandom.GetBytes(CryptoConstants.IvSize);

        var act = () => DekManager.UnwrapVersioned(bogus, iv, masterDek);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void UnwrapVersioned_TooShortToContainTag_Throws()
    {
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var tooShort = new byte[] { 0x01 };
        var iv = SecureRandom.GetBytes(CryptoConstants.IvSize);

        var act = () => DekManager.UnwrapVersioned(tooShort, iv, masterDek);
        act.Should().Throw<CryptographicException>();
    }
}

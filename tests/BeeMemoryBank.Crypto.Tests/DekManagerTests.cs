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
}

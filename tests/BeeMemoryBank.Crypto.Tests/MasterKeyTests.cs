using BeeMemoryBank.Crypto;
using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto.Tests;

public class MasterKeyTests
{
    [Fact]
    public void WrapUnwrap_Roundtrip()
    {
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var kek = SecureRandom.GetBytes(CryptoConstants.KeySize);

        var (ciphertext, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);
        var unwrapped = MasterKeyManager.UnwrapMasterDek(ciphertext, iv, kek);

        unwrapped.Should().Equal(masterDek);
    }

    [Fact]
    public void WrapUnwrap_WrongKek_Throws()
    {
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var kek = SecureRandom.GetBytes(CryptoConstants.KeySize);
        var wrongKek = SecureRandom.GetBytes(CryptoConstants.KeySize);

        var (ciphertext, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

        var act = () => MasterKeyManager.UnwrapMasterDek(ciphertext, iv, wrongKek);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void GenerateMasterDek_Is32Bytes()
    {
        var dek = MasterKeyManager.GenerateMasterDek();
        dek.Should().HaveCount(CryptoConstants.KeySize);
    }
}

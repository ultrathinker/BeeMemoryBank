using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

public class KeyDerivationTests
{
    [Fact]
    public void DeriveKek_SameInput_SameOutput()
    {
        var salt = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var kek1 = KeyDerivation.DeriveKek("password123", salt);
        var kek2 = KeyDerivation.DeriveKek("password123", salt);
        kek1.Should().Equal(kek2);
    }

    [Fact]
    public void DeriveKek_DifferentSalt_DifferentResult()
    {
        var salt1 = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var salt2 = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var kek1 = KeyDerivation.DeriveKek("password", salt1);
        var kek2 = KeyDerivation.DeriveKek("password", salt2);
        kek1.Should().NotEqual(kek2);
    }

    [Fact]
    public void DeriveKek_DifferentPassword_DifferentResult()
    {
        var salt = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var kek1 = KeyDerivation.DeriveKek("password1", salt);
        var kek2 = KeyDerivation.DeriveKek("password2", salt);
        kek1.Should().NotEqual(kek2);
    }

    [Fact]
    public void DeriveKek_OutputIs32Bytes()
    {
        var kek = KeyDerivation.DeriveKek("test", SecureRandom.GetBytes(CryptoConstants.SaltSize));
        kek.Should().HaveCount(CryptoConstants.KeySize);
    }
}

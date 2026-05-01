using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

public class Ed25519Tests
{
    [Fact]
    public void SignVerify_Roundtrip()
    {
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var data = "sync event"u8.ToArray();

        var signature = Ed25519Signer.Sign(privateKey, data);
        var valid = Ed25519Signer.Verify(publicKey, data, signature);

        valid.Should().BeTrue();
    }

    [Fact]
    public void Verify_TamperedData_Fails()
    {
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var signature = Ed25519Signer.Sign(privateKey, data);
        data[0] ^= 0xFF; // tamper with byte

        var valid = Ed25519Signer.Verify(publicKey, data, signature);
        valid.Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongPublicKey_Fails()
    {
        var (_, privateKey) = Ed25519Signer.GenerateKeyPair();
        var (wrongPublicKey, _) = Ed25519Signer.GenerateKeyPair();
        var data = "data"u8.ToArray();

        var signature = Ed25519Signer.Sign(privateKey, data);
        var valid = Ed25519Signer.Verify(wrongPublicKey, data, signature);

        valid.Should().BeFalse();
    }

    [Fact]
    public void GenerateKeyPair_KeySizes()
    {
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        publicKey.Should().HaveCount(CryptoConstants.Ed25519PublicKeySize);
        privateKey.Should().HaveCount(CryptoConstants.Ed25519PrivateKeySize);
    }

    [Fact]
    public void GenerateKeyPair_IsDifferentEachTime()
    {
        var (pub1, _) = Ed25519Signer.GenerateKeyPair();
        var (pub2, _) = Ed25519Signer.GenerateKeyPair();
        pub1.Should().NotEqual(pub2);
    }
}

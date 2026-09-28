namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// Identity row v=2 (plan 3.5): the seed is outside the database, and signing dispatches to the
/// external source instead of the master DEK — which a blind node never has.
/// </summary>
public class NodeIdentityExternalKeyTests
{
    private static readonly Guid NodeId = Guid.NewGuid();
    private static readonly byte[] Payload = "payload"u8.ToArray();

    [Fact]
    public void V2_SignsWithTheExternalSeed_AndNeverAsksForTheDek()
    {
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();

        byte[]? sig = null;
        var act = () => sig = NodeIdentityCrypto.SignWithIdentityOrGetDek(
            [], null, NodeIdentityCrypto.ExternalKeyVersion, NodeId,
            getMasterDek: () => throw new InvalidOperationException("DEK requested"),
            getExternalSeed: () => (byte[])seed.Clone(),
            Payload);

        act.Should().NotThrow("a v=2 node has no DEK to hand over");
        Ed25519Signer.Verify(pub, Payload, sig!).Should().BeTrue();
    }

    [Fact]
    public void V2_WithoutAnExternalSource_FailsSayingWhy()
    {
        var act = () => NodeIdentityCrypto.SignWithIdentityOrGetDek(
            [], null, NodeIdentityCrypto.ExternalKeyVersion, NodeId, () => new byte[32], Payload);

        act.Should().Throw<InvalidOperationException>().WithMessage("*outside the database*");
    }

    /// <summary>
    /// Every caller that decrypts the stored key (rotation, snapshot signing) must not treat an
    /// empty v=2 column as a v=1 ciphertext and fail somewhere deep in AES-GCM.
    /// </summary>
    [Fact]
    public void V2_HasNothingToDecrypt()
    {
        var act = () => NodeIdentityCrypto.GetDecryptedPrivateKey(
            [], null, NodeIdentityCrypto.ExternalKeyVersion, NodeId, new byte[32]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*outside the database*");
    }

    [Fact]
    public void V1_StillSignsUnderTheDek_WithAnExternalSourcePresent()
    {
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        var dek = new byte[32];
        var (wrapped, iv) = NodeIdentityCrypto.EncryptPrivateKey(seed, dek, NodeId);

        var sig = NodeIdentityCrypto.SignWithIdentityOrGetDek(
            wrapped, iv, 1, NodeId, () => (byte[])dek.Clone(),
            getExternalSeed: () => throw new InvalidOperationException("external key requested"),
            Payload);

        Ed25519Signer.Verify(pub, Payload, sig).Should().BeTrue();
    }
}

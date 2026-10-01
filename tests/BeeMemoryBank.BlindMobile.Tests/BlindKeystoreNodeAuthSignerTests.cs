using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindMobile.Tests;

public sealed class BlindKeystoreNodeAuthSignerTests
{
    [Fact]
    public void SignChallenge_UsesTheExternalV2Seed_AndClearsItsWorkingCopy()
    {
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var keys = new TrackingKeys(seed);
        var signer = new BlindKeystoreNodeAuthSigner(keys);
        var payload = "challenge"u8.ToArray();
        var identity = new NodeIdentity
        {
            NodeId = Guid.NewGuid(),
            DisplayName = "phone",
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion
        };

        var signature = signer.SignChallenge(identity, payload);

        Ed25519Signer.Verify(publicKey, payload, signature).Should().BeTrue();
        keys.LastLoadedSeed.Should().NotBeNull();
        keys.LastLoadedSeed!.Should().OnlyContain(b => b == 0,
            "the signer must clear the Keystore seed copy after signing");
    }

    [Fact]
    public void SignChallenge_RejectsAnIdentityThatIsNotExternalV2()
    {
        var (_, seed) = Ed25519Signer.GenerateKeyPair();
        var signer = new BlindKeystoreNodeAuthSigner(new TrackingKeys(seed));
        var identity = new NodeIdentity { NodeId = Guid.NewGuid(), Ed25519PrivateKeyV = 1 };

        var act = () => signer.SignChallenge(identity, "challenge"u8.ToArray());

        act.Should().Throw<InvalidOperationException>().WithMessage("*v=2*");
    }

    private sealed class TrackingKeys(byte[] seed) : IBlindNodeKeys
    {
        public byte[]? LastLoadedSeed { get; private set; }

        public byte[]? LoadIdentitySeed()
        {
            LastLoadedSeed = seed.ToArray();
            return LastLoadedSeed;
        }

        public void SaveIdentitySeed(byte[] value) { }
        public void SaveBackupKey(byte[] key) { }
        public byte[]? LoadBackupKey() => null;
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }
}

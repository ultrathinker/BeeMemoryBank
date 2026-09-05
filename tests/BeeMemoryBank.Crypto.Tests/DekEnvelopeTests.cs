using System.Security.Cryptography;
using BeeMemoryBank.Crypto;
using Org.BouncyCastle.Crypto.Parameters;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// Tests for the per-peer X25519 envelopes that make a DEK rotation confidential (ADR 0006).
///
/// <para>
/// Correctness of the Ed25519 → Curve25519 birational maps is anchored two ways: an external RFC 7748
/// known-answer vector for the raw X25519 primitive the envelopes run over, and the libsodium identity
/// <c>pk_to_curve(seed·B) == base·sk_to_curve(seed)</c> — computed independently via BouncyCastle's own
/// X25519 base multiplication — for the maps themselves. If the y-decoding endianness, the
/// (1+y)/(1-y) formula direction, the modular inverse, or the u-encoding were wrong, the initiator's
/// derivation of a peer's public key and the peer's derivation of its own private key would not agree,
/// and that identity would fail.
/// </para>
/// </summary>
public class DekEnvelopeTests
{
    // ---- Ed25519 → X25519 private scalar -------------------------------------------------

    [Fact]
    public void SeedToX25519Private_MatchesSha512ClampKnownAnswer()
    {
        // crypto_sign_ed25519_sk_to_curve25519: SHA-512(seed)[0..32], clamped.
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)i;

        var expected = SHA512.HashData(seed)[..32];
        expected[0] &= 248;
        expected[31] &= 127;
        expected[31] |= 64;

        var actual = DekEnvelope.Ed25519SeedToX25519PrivateKey(seed);

        actual.Should().Equal(expected);
        actual.Should().HaveCount(32);
        (actual[0] & 0x07).Should().Be(0, "the low 3 bits must be cleared by clamping");
        (actual[31] & 0x80).Should().Be(0, "the top bit must be cleared by clamping");
        (actual[31] & 0x40).Should().Be(0x40, "bit 254 must be set by clamping");
    }

    // ---- Ed25519 → X25519 public: the libsodium identity ---------------------------------

    [Fact]
    public void PublicKeyToX25519_EqualsBasePointOfDerivedPrivate_FixedSeed()
    {
        // Deterministic known-answer: a fixed Ed25519 seed yields a fixed Ed25519 public key, and the
        // Montgomery image of that public key must equal the X25519 public key of the private scalar
        // derived from the same seed — pk_to_curve(seed·B) == base·sk_to_curve(seed).
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)(0xA0 ^ i);

        var edPub = new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();

        var viaPublicMap = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(edPub);
        var viaPrivateBase = X25519BasePoint(DekEnvelope.Ed25519SeedToX25519PrivateKey(seed));

        viaPublicMap.Should().Equal(viaPrivateBase);
    }

    [Fact]
    public void PublicKeyToX25519_EqualsBasePointOfDerivedPrivate_RandomKeys()
    {
        for (var i = 0; i < 16; i++)
        {
            var (edPub, edSeed) = Ed25519Signer.GenerateKeyPair();

            var viaPublicMap = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(edPub);
            var viaPrivateBase = X25519BasePoint(DekEnvelope.Ed25519SeedToX25519PrivateKey(edSeed));

            viaPublicMap.Should().Equal(viaPrivateBase, "iteration {0}", i);
        }
    }

    // ---- Raw X25519 primitive: RFC 7748 §6.1 external vector ------------------------------

    [Fact]
    public void X25519_Rfc7748_KnownAnswer()
    {
        var alicePriv = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var alicePub = Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var bobPriv = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var bobPub = Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        var expectedShared = Convert.FromHexString("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        X25519BasePoint(alicePriv).Should().Equal(alicePub);
        X25519BasePoint(bobPriv).Should().Equal(bobPub);

        var sharedAB = X25519Agree(alicePriv, bobPub);
        var sharedBA = X25519Agree(bobPriv, alicePub);
        sharedAB.Should().Equal(expectedShared);
        sharedBA.Should().Equal(expectedShared);
    }

    [Fact]
    public void X25519_AgreementIsSymmetric_ForDerivedKeys()
    {
        var (edPubA, edSeedA) = Ed25519Signer.GenerateKeyPair();
        var (edPubB, edSeedB) = Ed25519Signer.GenerateKeyPair();

        var privA = DekEnvelope.Ed25519SeedToX25519PrivateKey(edSeedA);
        var privB = DekEnvelope.Ed25519SeedToX25519PrivateKey(edSeedB);
        var pubA = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(edPubA);
        var pubB = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(edPubB);

        X25519Agree(privA, pubB).Should().Equal(X25519Agree(privB, pubA));
    }

    // ---- Envelope round-trip -------------------------------------------------------------

    [Fact]
    public void Envelope_OpensForEachIntendedRecipient()
    {
        var (edPubA, edSeedA) = Ed25519Signer.GenerateKeyPair();
        var (edPubB, edSeedB) = Ed25519Signer.GenerateKeyPair();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var newDek = RandomNumberGenerator.GetBytes(32);

        var set = DekEnvelope.Build(newDek, rotationId, new[]
        {
            new DekEnvelope.Recipient(nodeA, edPubA),
            new DekEnvelope.Recipient(nodeB, edPubB),
        });

        var boxA = set.Peers[nodeA.ToString().ToUpperInvariant()];
        var boxB = set.Peers[nodeB.ToString().ToUpperInvariant()];

        DekEnvelope.Open(set.EphemeralPublicKeyB64, boxA.WrappedB64, boxA.NonceB64, rotationId, nodeA, edSeedA)
            .Should().Equal(newDek);
        DekEnvelope.Open(set.EphemeralPublicKeyB64, boxB.WrappedB64, boxB.NonceB64, rotationId, nodeB, edSeedB)
            .Should().Equal(newDek);
    }

    [Fact]
    public void Envelope_PeersAreKeyedByUppercaseNodeId_AndDeduped()
    {
        var (edPub, _) = Ed25519Signer.GenerateKeyPair();
        var node = Guid.NewGuid();
        var set = DekEnvelope.Build(RandomNumberGenerator.GetBytes(32), Guid.NewGuid(), new[]
        {
            new DekEnvelope.Recipient(node, edPub),
            new DekEnvelope.Recipient(node, edPub), // duplicate — must collapse to one entry
        });

        set.Peers.Should().ContainKey(node.ToString().ToUpperInvariant());
        set.Peers.Should().HaveCount(1);
    }

    [Fact]
    public void Envelope_EachPeerGetsADistinctNonceAndCiphertext()
    {
        var (edPubA, _) = Ed25519Signer.GenerateKeyPair();
        var (edPubB, _) = Ed25519Signer.GenerateKeyPair();
        var set = DekEnvelope.Build(RandomNumberGenerator.GetBytes(32), Guid.NewGuid(), new[]
        {
            new DekEnvelope.Recipient(Guid.NewGuid(), edPubA),
            new DekEnvelope.Recipient(Guid.NewGuid(), edPubB),
        });

        var boxes = set.Peers.Values.ToList();
        boxes.Should().HaveCount(2);
        boxes[0].NonceB64.Should().NotBe(boxes[1].NonceB64, "each envelope uses its own random nonce");
        boxes[0].WrappedB64.Should().NotBe(boxes[1].WrappedB64, "each peer has a distinct wrap key");
    }

    // ---- Negative cases: the AAD/salt/tag binding ----------------------------------------

    [Fact]
    public void Open_WithAnotherNodesIdentity_Throws()
    {
        var (edPubA, edSeedA) = Ed25519Signer.GenerateKeyPair();
        var (edPubB, _) = Ed25519Signer.GenerateKeyPair();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var newDek = RandomNumberGenerator.GetBytes(32);

        var set = DekEnvelope.Build(newDek, rotationId, new[]
        {
            new DekEnvelope.Recipient(nodeA, edPubA),
            new DekEnvelope.Recipient(nodeB, edPubB),
        });
        var boxB = set.Peers[nodeB.ToString().ToUpperInvariant()];

        // Node A (its own identity and node id) tries to open node B's envelope. Its shared secret and
        // its AAD/info both differ from B's, so the GCM tag cannot verify.
        var attempt = () => DekEnvelope.Open(
            set.EphemeralPublicKeyB64, boxB.WrappedB64, boxB.NonceB64, rotationId, nodeA, edSeedA);

        attempt.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Open_WithWrongRotationId_Throws()
    {
        var (edPub, edSeed) = Ed25519Signer.GenerateKeyPair();
        var node = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var newDek = RandomNumberGenerator.GetBytes(32);

        var set = DekEnvelope.Build(newDek, rotationId, new[] { new DekEnvelope.Recipient(node, edPub) });
        var box = set.Peers[node.ToString().ToUpperInvariant()];

        var attempt = () => DekEnvelope.Open(
            set.EphemeralPublicKeyB64, box.WrappedB64, box.NonceB64, Guid.NewGuid(), node, edSeed);

        attempt.Should().Throw<CryptographicException>("the rotation id salts the HKDF, so the wrap key differs");
    }

    [Fact]
    public void Open_WithTamperedCiphertext_Throws()
    {
        var (edPub, edSeed) = Ed25519Signer.GenerateKeyPair();
        var node = Guid.NewGuid();
        var rotationId = Guid.NewGuid();

        var set = DekEnvelope.Build(RandomNumberGenerator.GetBytes(32), rotationId,
            new[] { new DekEnvelope.Recipient(node, edPub) });
        var box = set.Peers[node.ToString().ToUpperInvariant()];

        var tampered = Convert.FromBase64String(box.WrappedB64);
        tampered[0] ^= 0xFF;

        var attempt = () => DekEnvelope.Open(
            set.EphemeralPublicKeyB64, Convert.ToBase64String(tampered), box.NonceB64, rotationId, node, edSeed);

        attempt.Should().Throw<CryptographicException>();
    }

    // ---- helpers -------------------------------------------------------------------------

    private static byte[] X25519BasePoint(byte[] privateScalar)
        => new X25519PrivateKeyParameters(privateScalar, 0).GeneratePublicKey().GetEncoded();

    private static byte[] X25519Agree(byte[] privateScalar, byte[] peerPublic)
    {
        var priv = new X25519PrivateKeyParameters(privateScalar, 0);
        var secret = new byte[32];
        priv.GenerateSecret(new X25519PublicKeyParameters(peerPublic, 0), secret, 0);
        return secret;
    }
}

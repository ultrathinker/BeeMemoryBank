using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.ReleaseTool.Tests;

public class ReleaseToolTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Convenience: sign <paramref name="data"/> with a freshly-generated key
    /// and return (publicKey, privateKey, signature).
    /// </summary>
    private static (byte[] publicKey, byte[] privateKey, byte[] signature) SignData(byte[] data)
    {
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        byte[] signature = Ed25519Signer.Sign(privateKey, data);
        return (publicKey, privateKey, signature);
    }

    // ── 1. Roundtrip: sign then verify succeeds ───────────────────────────────

    [Fact]
    public void SignThenVerify_SameKey_Succeeds()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("""{"version":"1.0.0","artifacts":[]}""");
        var (publicKey, _, signature) = SignData(data);

        // Act
        bool valid = Ed25519Signer.Verify(publicKey, data, signature);

        // Assert
        valid.Should().BeTrue("a signature made with the matching private key must verify");
    }

    [Fact]
    public void SignThenVerify_RoundtripViaBase64_Succeeds()
    {
        // Ensures the base64 encode/decode round-trip used by the CLI preserves keys and signatures.
        byte[] data = Encoding.UTF8.GetBytes("releases manifest content");
        var (pubKey, privKey, _) = Ed25519Signer.GenerateKeyPair() switch
        {
            var kp => (kp.publicKey, kp.privateKey, (byte[])null!)
        };

        // Simulate CLI file I/O: encode to base64, write string, read string, decode
        string privKeyB64 = Convert.ToBase64String(privKey);
        string pubKeyB64  = Convert.ToBase64String(pubKey);

        byte[] privKeyRestored = Convert.FromBase64String(privKeyB64.Trim());
        byte[] pubKeyRestored  = Convert.FromBase64String(pubKeyB64.Trim());

        byte[] sig = Ed25519Signer.Sign(privKeyRestored, data);
        string sigB64 = Convert.ToBase64String(sig);
        byte[] sigRestored = Convert.FromBase64String(sigB64.Trim());

        bool valid = Ed25519Signer.Verify(pubKeyRestored, data, sigRestored);

        valid.Should().BeTrue("base64 round-trip must not corrupt keys or signatures");
    }

    // ── 2. Verify fails for a signature made with a different key ─────────────

    [Fact]
    public void Verify_DifferentKey_Fails()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("some release manifest");

        // Sign with key A
        var (_, _, signature) = SignData(data);

        // Generate a completely independent key B
        var (differentPublicKey, _) = Ed25519Signer.GenerateKeyPair();

        // Act: try to verify the signature from key A using public key B
        bool valid = Ed25519Signer.Verify(differentPublicKey, data, signature);

        // Assert
        valid.Should().BeFalse("a signature from one key must not verify against a different public key");
    }

    // ── 3. Verify fails if the signed file's bytes are altered (tamper detection) ─

    [Fact]
    public void Verify_TamperedData_Fails()
    {
        // Arrange — sign the original bytes
        byte[] original = Encoding.UTF8.GetBytes("""{"version":"1.2.3","artifacts":[{"url":"...","sha256":"abc"}]}""");
        var (publicKey, _, signature) = SignData(original);

        // Tamper: change one byte in the payload
        byte[] tampered = (byte[])original.Clone();
        tampered[10] ^= 0xFF; // flip bits in one byte

        // Act
        bool valid = Ed25519Signer.Verify(publicKey, tampered, signature);

        // Assert
        valid.Should().BeFalse("a tampered file must not pass signature verification");
    }

    [Fact]
    public void Verify_AppendedByte_Fails()
    {
        // Arrange — also cover the case where an attacker appends data
        byte[] original  = Encoding.UTF8.GetBytes("""{"version":"2.0.0"}""");
        var (publicKey, _, signature) = SignData(original);

        byte[] tampered = original.Append((byte)0x00).ToArray();

        // Act
        bool valid = Ed25519Signer.Verify(publicKey, tampered, signature);

        // Assert
        valid.Should().BeFalse("appending bytes to the signed file must invalidate the signature");
    }

    [Fact]
    public void Verify_TruncatedData_Fails()
    {
        // Arrange — cover the case where an attacker truncates data
        byte[] original = Encoding.UTF8.GetBytes("""{"version":"2.0.0","artifacts":[]}""");
        var (publicKey, _, signature) = SignData(original);

        byte[] truncated = original[..^1]; // remove last byte

        // Act
        bool valid = Ed25519Signer.Verify(publicKey, truncated, signature);

        // Assert
        valid.Should().BeFalse("a truncated file must not pass signature verification");
    }

    // ── 4. Signing is deterministic ───────────────────────────────────────────

    [Fact]
    public void Sign_IsDeterministic()
    {
        // Ed25519 is deterministic — same (key, data) always produces the same signature.
        byte[] data = Encoding.UTF8.GetBytes("determinism check");
        var (_, privateKey) = Ed25519Signer.GenerateKeyPair();

        byte[] sig1 = Ed25519Signer.Sign(privateKey, data);
        byte[] sig2 = Ed25519Signer.Sign(privateKey, data);

        sig1.Should().Equal(sig2, "Ed25519 must produce identical signatures for the same inputs");
    }
}

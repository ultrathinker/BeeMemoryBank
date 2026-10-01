using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Ed25519 signatures and verification for sync events.
/// Implementation via BouncyCastle (fully managed, works on Android/iOS/Desktop).
/// Key format is compatible with NSec: private key is a 32-byte seed.
/// </summary>
public static class Ed25519Signer
{
    /// <summary>Generates a new Ed25519 key pair. Private key is a 32-byte seed.</summary>
    public static (byte[] publicKey, byte[] privateKey) GenerateKeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom()));
        var keyPair = generator.GenerateKeyPair();

        var privateKeyParams = (Ed25519PrivateKeyParameters)keyPair.Private;
        var publicKeyParams = (Ed25519PublicKeyParameters)keyPair.Public;

        return (publicKeyParams.GetEncoded(), privateKeyParams.GetEncoded());
    }

    /// <summary>Signs data with the private key (seed, 32 bytes).</summary>
    public static byte[] Sign(byte[] privateKey, byte[] data)
    {
        var privateKeyParams = new Ed25519PrivateKeyParameters(privateKey);
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, privateKeyParams);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Verifies signature with the public key (32 bytes).</summary>
    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        var publicKeyParams = new Ed25519PublicKeyParameters(publicKey);
        var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        verifier.Init(false, publicKeyParams);
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature);
    }
}

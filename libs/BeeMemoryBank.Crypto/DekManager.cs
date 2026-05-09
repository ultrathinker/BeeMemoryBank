using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

public static class DekManager
{
    private const byte Version1 = 0x01;
    private const int LegacyWrappedDekSize = CryptoConstants.KeySize + CryptoConstants.TagSize;
    private const int VersionedWrappedDekSize = LegacyWrappedDekSize + 1;

    public static byte[] GenerateArticleDek() => SecureRandom.GetBytes(CryptoConstants.KeySize);

    public static (byte[] wrapped, byte[] iv) WrapDek(byte[] articleDek, byte[] masterDek, byte[]? aad = null)
    {
        var (encrypted, iv) = AesGcmHelper.Encrypt(masterDek, articleDek, aad);
        var versioned = new byte[1 + encrypted.Length];
        versioned[0] = Version1;
        encrypted.CopyTo(versioned, 1);
        return (versioned, iv);
    }

    public static byte[] UnwrapDek(byte[] wrapped, byte[] iv, byte[] masterDek, byte[]? aad = null)
    {
        // Strict length-based dispatch — eliminates ambiguity that previously allowed
        // an attacker with DB write access to substitute v0 blobs into v1 rows and
        // bypass AAD via the silent fallback path.
        if (wrapped.Length == LegacyWrappedDekSize)
        {
            // v0 — no version byte, no AAD
            return AesGcmHelper.Decrypt(masterDek, wrapped, iv, aad: null);
        }
        if (wrapped.Length == VersionedWrappedDekSize && wrapped[0] == Version1)
        {
            var stripped = wrapped[1..];
            return AesGcmHelper.Decrypt(masterDek, stripped, iv, aad);
        }
        throw new CryptographicException(
            $"Invalid wrapped DEK length: {wrapped.Length} (expected {LegacyWrappedDekSize} for v0 or {VersionedWrappedDekSize} for v1).");
    }
}

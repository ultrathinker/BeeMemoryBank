using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Utilities for working with agent keys: generation, hashing, password encryption.
/// </summary>
public static class AgentKeyHelper
{
    /// <summary>Generates API key of format bee_ + 32 hex characters (16 random bytes).</summary>
    public static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return "bee_" + Convert.ToHexString(bytes).ToLower();
    }

    /// <summary>SHA256 hash of the key for database lookup.</summary>
    public static string ComputeKeyHash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLower();
    }

    /// <summary>Derives a 32-byte encryption key from the API key (differs from key_hash).</summary>
    private static byte[] DeriveEncryptionKey(string apiKey)
        => SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + "bmb-encrypt"));

    /// <summary>Encrypts Master DEK with a key derived from the API key.</summary>
    public static (byte[] ciphertext, byte[] iv) EncryptDek(string apiKey, byte[] masterDek)
    {
        var encKey = DeriveEncryptionKey(apiKey);
        return AesGcmHelper.Encrypt(encKey, masterDek);
    }

    /// <summary>Decrypts Master DEK with a key derived from the API key.</summary>
    public static byte[] DecryptDek(string apiKey, byte[] ciphertext, byte[] iv)
    {
        var encKey = DeriveEncryptionKey(apiKey);
        return AesGcmHelper.Decrypt(encKey, ciphertext, iv);
    }

    /// <summary>Key prefix for UI display (bee_xxxx****).</summary>
    public static string GetKeyPrefix(string apiKey)
        => apiKey.Length > 12 ? apiKey[..12] : apiKey;
}

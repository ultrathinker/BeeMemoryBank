using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Utilities for working with agent keys: generation, hashing, password encryption.
/// </summary>
public static class AgentKeyHelper
{
    private static readonly byte[] EncryptionInfoV1 = "bmb-agent-dek-encryption-v1"u8.ToArray();

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

    /// <summary>Derives a 32-byte encryption key from the API key (legacy SHA256 path).</summary>
    private static byte[] DeriveEncryptionKey(string apiKey)
        => SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + "bmb-encrypt"));

    /// <summary>Derives a 32-byte encryption key using HKDF-SHA256 (V1 path).</summary>
    public static byte[] DeriveEncryptionKeyV1(string apiKey, byte[] salt)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(apiKey), 32, salt, EncryptionInfoV1);
    }

    /// <summary>Encrypts Master DEK with a key derived from the API key (Legacy).</summary>
    public static (byte[] ciphertext, byte[] iv) EncryptDek(string apiKey, byte[] masterDek)
    {
        var encKey = DeriveEncryptionKey(apiKey);
        try
        {
            return AesGcmHelper.Encrypt(encKey, masterDek);
        }
        finally
        {
            Array.Clear(encKey);
        }
    }

    /// <summary>Encrypts Master DEK with HKDF-SHA256 and per-row salt (V1).</summary>
    public static (byte[] ciphertext, byte[] iv, byte[] salt) EncryptDekV1(string apiKey, byte[] masterDek)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var encKey = DeriveEncryptionKeyV1(apiKey, salt);
        try
        {
            var (ciphertext, iv) = AesGcmHelper.Encrypt(encKey, masterDek);
            return (ciphertext, iv, salt);
        }
        finally
        {
            Array.Clear(encKey);
        }
    }

    /// <summary>Decrypts Master DEK with a key derived from the API key (Legacy).</summary>
    public static byte[] DecryptDek(string apiKey, byte[] ciphertext, byte[] iv)
    {
        var encKey = DeriveEncryptionKey(apiKey);
        try
        {
            return AesGcmHelper.Decrypt(encKey, ciphertext, iv);
        }
        finally
        {
            Array.Clear(encKey);
        }
    }

    /// <summary>Decrypts Master DEK using HKDF-SHA256 and provided salt (V1).</summary>
    public static byte[] DecryptDekV1(string apiKey, byte[] ciphertext, byte[] iv, byte[] salt)
    {
        var encKey = DeriveEncryptionKeyV1(apiKey, salt);
        try
        {
            return AesGcmHelper.Decrypt(encKey, ciphertext, iv);
        }
        finally
        {
            Array.Clear(encKey);
        }
    }

    /// <summary>Key prefix for UI display (bee_xxxx****).</summary>
    public static string GetKeyPrefix(string apiKey)
        => apiKey.Length > 12 ? apiKey[..12] : apiKey;
}

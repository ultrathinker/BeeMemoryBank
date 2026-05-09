using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Internal AES-256-GCM helper. The tag (16 bytes) is concatenated with ciphertext.
/// </summary>
internal static class AesGcmHelper
{
    /// <summary>
    /// Encrypts data. Returns (ciphertext || tag, iv).
    /// </summary>
    internal static (byte[] ciphertextWithTag, byte[] iv) Encrypt(byte[] key, byte[] plaintext, byte[]? aad = null)
    {
        var iv = SecureRandom.GetBytes(CryptoConstants.IvSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[CryptoConstants.TagSize];

        using var aes = new AesGcm(key, CryptoConstants.TagSize);
        aes.Encrypt(iv, plaintext, ciphertext, tag, aad);

        var result = new byte[ciphertext.Length + CryptoConstants.TagSize];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);

        Array.Clear(ciphertext);
        Array.Clear(tag);

        return (result, iv);
    }

    /// <summary>
    /// Decrypts data. Accepts (ciphertext || tag) and iv.
    /// Throws <see cref="AuthenticationTagMismatchException"/> on data tampering.
    /// </summary>
    internal static byte[] Decrypt(byte[] key, byte[] ciphertextWithTag, byte[] iv, byte[]? aad = null)
    {
        if (ciphertextWithTag.Length < CryptoConstants.TagSize)
            throw new CryptographicException("Ciphertext too short.");

        var ciphertextLen = ciphertextWithTag.Length - CryptoConstants.TagSize;
        var ciphertextOnly = ciphertextWithTag[..ciphertextLen];
        var tag = ciphertextWithTag[ciphertextLen..];
        var plaintext = new byte[ciphertextLen];

        using var aes = new AesGcm(key, CryptoConstants.TagSize);
        aes.Decrypt(iv, ciphertextOnly, tag, plaintext, aad);

        return plaintext;
    }
}

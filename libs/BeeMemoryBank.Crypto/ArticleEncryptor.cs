using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Encryption and decryption of article body using article DEK (AES-256-GCM).
/// </summary>
public static class ArticleEncryptor
{
    /// <summary>Encrypts article body. Each call generates a unique IV.</summary>
    public static (byte[] ciphertext, byte[] iv) Encrypt(string plaintext, byte[] articleDek)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        return AesGcmHelper.Encrypt(articleDek, plaintextBytes);
    }

    /// <summary>Decrypts article body.</summary>
    public static string Decrypt(byte[] ciphertext, byte[] iv, byte[] articleDek)
    {
        var plaintextBytes = AesGcmHelper.Decrypt(articleDek, ciphertext, iv);
        return Encoding.UTF8.GetString(plaintextBytes);
    }
}

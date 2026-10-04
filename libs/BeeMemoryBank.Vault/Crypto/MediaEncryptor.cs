namespace BeeMemoryBank.Crypto;

public static class MediaEncryptor
{
    public static (byte[] ciphertext, byte[] iv) Encrypt(byte[] plaintext, byte[] dek, byte[]? aad = null)
        => AesGcmHelper.Encrypt(dek, plaintext, aad);

    public static byte[] Decrypt(byte[] ciphertext, byte[] iv, byte[] dek, byte[]? aad = null)
        => AesGcmHelper.Decrypt(dek, ciphertext, iv, aad);
}

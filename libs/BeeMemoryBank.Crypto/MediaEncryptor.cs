namespace BeeMemoryBank.Crypto;

public static class MediaEncryptor
{
    public static (byte[] ciphertext, byte[] iv) Encrypt(byte[] plaintext, byte[] dek)
        => AesGcmHelper.Encrypt(dek, plaintext);

    public static byte[] Decrypt(byte[] ciphertext, byte[] iv, byte[] dek)
        => AesGcmHelper.Decrypt(dek, ciphertext, iv);
}

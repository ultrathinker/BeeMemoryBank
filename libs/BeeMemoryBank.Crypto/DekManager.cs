namespace BeeMemoryBank.Crypto;

/// <summary>
/// Generation and wrap/unwrap of per-article DEK via master DEK (AES-256-GCM).
/// </summary>
public static class DekManager
{
    public static byte[] GenerateArticleDek() => SecureRandom.GetBytes(CryptoConstants.KeySize);

    /// <summary>Wraps article DEK with master DEK.</summary>
    public static (byte[] wrapped, byte[] iv) WrapDek(byte[] articleDek, byte[] masterDek)
        => AesGcmHelper.Encrypt(masterDek, articleDek);

    /// <summary>Unwraps article DEK with master DEK.</summary>
    public static byte[] UnwrapDek(byte[] wrapped, byte[] iv, byte[] masterDek)
        => AesGcmHelper.Decrypt(masterDek, wrapped, iv);
}

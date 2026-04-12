namespace BeeMemoryBank.Crypto;

/// <summary>
/// Generation and wrap/unwrap of master DEK via KEK (AES-256-GCM).
/// </summary>
public static class MasterKeyManager
{
    public static byte[] GenerateMasterDek() => SecureRandom.GetBytes(CryptoConstants.KeySize);

    /// <summary>Wraps master DEK with KEK.</summary>
    public static (byte[] ciphertext, byte[] iv) WrapMasterDek(byte[] masterDek, byte[] kek)
        => AesGcmHelper.Encrypt(kek, masterDek);

    /// <summary>
    /// Unwraps master DEK.
    /// Throws an exception if the KEK is incorrect (GCM auth tag won't match).
    /// </summary>
    public static byte[] UnwrapMasterDek(byte[] ciphertext, byte[] iv, byte[] kek)
        => AesGcmHelper.Decrypt(kek, ciphertext, iv);

    private static readonly byte[] SentinelPlaintext = "BeeMemoryBank"u8.ToArray();

    /// <summary>
    /// Creates sentinel: AES-256-GCM("BeeMemoryBank", masterDEK).
    /// Format: iv || ciphertextWithTag
    /// </summary>
    public static byte[] ComputeSentinel(byte[] masterDek)
    {
        var (ciphertextWithTag, iv) = AesGcmHelper.Encrypt(masterDek, SentinelPlaintext);
        var result = new byte[iv.Length + ciphertextWithTag.Length];
        iv.CopyTo(result, 0);
        ciphertextWithTag.CopyTo(result, iv.Length);
        return result;
    }

    /// <summary>
    /// Verifies sentinel. Returns true if masterDEK is correct.
    /// </summary>
    public static bool VerifySentinel(byte[] sentinel, byte[] masterDek)
    {
        try
        {
            var iv = sentinel[..CryptoConstants.IvSize];
            var ciphertextWithTag = sentinel[CryptoConstants.IvSize..];
            var plaintext = AesGcmHelper.Decrypt(masterDek, ciphertextWithTag, iv);
            // AUDIT NOTE: SequenceEqual is non-constant-time but NOT a timing oracle here.
            // AES-GCM decrypt above is already authenticated — if it succeeds, the plaintext
            // is guaranteed correct. This comparison is a redundant safety check, not a secret gate.
            return plaintext.SequenceEqual(SentinelPlaintext);
        }
        catch { return false; }
    }
}

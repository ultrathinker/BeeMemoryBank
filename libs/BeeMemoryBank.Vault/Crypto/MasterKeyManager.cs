using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

public static class MasterKeyManager
{
    private const byte Version1 = 0x01;
    private const int LegacyWrappedDekSize = CryptoConstants.KeySize + CryptoConstants.TagSize;
    private const int VersionedWrappedDekSize = LegacyWrappedDekSize + 1;
    // Sentinel plaintext "BeeMemoryBank" = 13 bytes UTF-8 → ct=13, tag=16.
    private const int SentinelPlaintextSize = 13;
    private const int LegacySentinelBodySize = SentinelPlaintextSize + CryptoConstants.TagSize; // 29
    private const int VersionedSentinelBodySize = LegacySentinelBodySize + 1; // 30
    private static readonly byte[] MasterDekAad = "bmb-master-dek"u8.ToArray();
    private static readonly byte[] SentinelAad = "bmb-sentinel"u8.ToArray();
    private static readonly byte[] SentinelPlaintext = "BeeMemoryBank"u8.ToArray();

    public static byte[] GenerateMasterDek() => SecureRandom.GetBytes(CryptoConstants.KeySize);

    public static (byte[] ciphertext, byte[] iv) WrapMasterDek(byte[] masterDek, byte[] kek)
    {
        var (encrypted, iv) = AesGcmHelper.Encrypt(kek, masterDek, MasterDekAad);
        var versioned = new byte[1 + encrypted.Length];
        versioned[0] = Version1;
        encrypted.CopyTo(versioned, 1);
        return (versioned, iv);
    }

    public static byte[] UnwrapMasterDek(byte[] ciphertext, byte[] iv, byte[] kek)
    {
        // Strict length-based dispatch (no silent fallback).
        if (ciphertext.Length == LegacyWrappedDekSize)
        {
            // v0 — no version byte, no AAD
            return AesGcmHelper.Decrypt(kek, ciphertext, iv, aad: null);
        }
        if (ciphertext.Length == VersionedWrappedDekSize && ciphertext[0] == Version1)
        {
            var stripped = ciphertext[1..];
            return AesGcmHelper.Decrypt(kek, stripped, iv, MasterDekAad);
        }
        throw new CryptographicException(
            $"Invalid wrapped master DEK length: {ciphertext.Length} (expected {LegacyWrappedDekSize} for v0 or {VersionedWrappedDekSize} for v1).");
    }

    public static byte[] ComputeSentinel(byte[] masterDek)
    {
        var (ciphertextWithTag, iv) = AesGcmHelper.Encrypt(masterDek, SentinelPlaintext, SentinelAad);
        var result = new byte[iv.Length + 1 + ciphertextWithTag.Length];
        iv.CopyTo(result, 0);
        result[iv.Length] = Version1;
        ciphertextWithTag.CopyTo(result, iv.Length + 1);
        return result;
    }

    public static bool VerifySentinel(byte[] sentinel, byte[] masterDek)
    {
        ArgumentNullException.ThrowIfNull(sentinel);
        ArgumentNullException.ThrowIfNull(masterDek);
        try
        {
            if (sentinel.Length < CryptoConstants.IvSize)
                return false;

            var iv = sentinel[..CryptoConstants.IvSize];
            var rest = sentinel[CryptoConstants.IvSize..];

            byte[] plaintext;
            // Strict length-based dispatch on body length.
            if (rest.Length == VersionedSentinelBodySize && rest[0] == Version1)
            {
                var ciphertextWithTag = rest[1..];
                plaintext = AesGcmHelper.Decrypt(masterDek, ciphertextWithTag, iv, SentinelAad);
            }
            else if (rest.Length == LegacySentinelBodySize)
            {
                // v0 — no version byte, no AAD
                plaintext = AesGcmHelper.Decrypt(masterDek, rest, iv, aad: null);
            }
            else
            {
                return false;
            }

            try
            {
                return plaintext.SequenceEqual(SentinelPlaintext);
            }
            finally
            {
                Array.Clear(plaintext);
            }
        }
        catch (CryptographicException) { return false; }
    }
}

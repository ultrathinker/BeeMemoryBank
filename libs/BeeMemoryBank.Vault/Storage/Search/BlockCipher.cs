using System.Security.Cryptography;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// AES-256-GCM encrypt/decrypt for one arbitrary-length segment block, keyed by the index key
/// (see <see cref="EncryptedSegmentStore"/>).
///
/// <para>
/// <b>Why this exists instead of calling <see cref="DekManager.WrapDek"/>/<see cref="DekManager.UnwrapDek"/>
/// directly</b> (as the segment store's 32-byte index-key wrapping does):
/// <c>DekManager.UnwrapDek</c> dispatches on the wrapped blob's exact byte LENGTH (48 bytes for
/// its legacy v0 framing, 49 for v1) to decide how to frame the AES-GCM call -- deliberate, to
/// keep the two wire formats unambiguous. That is correct for exactly-32-byte secrets
/// (per-entity DEKs, the index key -- a 32-byte plaintext always wraps to exactly 49 bytes), but
/// it makes <c>UnwrapDek</c> unable to unwrap a payload of any OTHER plaintext length: a 64 KiB
/// block wraps fine via <c>WrapDek</c> (which has no length restriction), but <c>UnwrapDek</c>
/// then throws <c>CryptographicException("Invalid wrapped DEK length...")</c> because the
/// wrapped length is neither 48 nor 49.
/// </para>
///
/// <para>
/// Given that, block-sized (~64 KiB) segment data cannot be routed through
/// <c>DekManager</c> at all. This class is the minimal workaround: the exact same
/// primitive (<see cref="AesGcm"/>), the exact same sizing
/// (<see cref="CryptoConstants.IvSize"/>/<see cref="CryptoConstants.TagSize"/>), and the exact
/// same iv-separate / ciphertext‖tag framing convention that <c>BeeMemoryBank.Crypto.AesGcmHelper</c>
/// (the <c>internal</c> class <c>DekManager</c> itself calls) already uses -- reimplemented here
/// only because <c>AesGcmHelper</c> is <c>internal</c> to a different assembly (inaccessible from
/// this project) and because <c>DekManager</c>'s public wrapper cannot carry arbitrary-length
/// payloads, as shown above. No new cipher, mode, or parameter choice is introduced here: this is
/// the same AES-256-GCM construction the rest of the codebase already uses, called directly
/// instead of through an inaccessible/size-limited wrapper.
/// </para>
/// </summary>
internal static class BlockCipher
{
    /// <summary>Encrypts <paramref name="plaintext"/> under <paramref name="key"/> and <paramref name="aad"/>. Returns (ciphertext‖tag, iv).</summary>
    public static (byte[] CiphertextWithTag, byte[] Iv) Encrypt(byte[] key, byte[] plaintext, byte[] aad)
    {
        byte[] iv = SecureRandom.GetBytes(CryptoConstants.IvSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[CryptoConstants.TagSize];

        using (var aes = new AesGcm(key, CryptoConstants.TagSize))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag, aad);
        }

        byte[] result = new byte[ciphertext.Length + CryptoConstants.TagSize];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);

        Array.Clear(ciphertext);
        Array.Clear(tag);

        return (result, iv);
    }

    /// <summary>
    /// Decrypts a (ciphertext‖tag) blob produced by <see cref="Encrypt"/>. Throws
    /// <see cref="CryptographicException"/> (including its
    /// <see cref="AuthenticationTagMismatchException"/> subclass, exactly like
    /// <c>AesGcmHelper.Decrypt</c>) if <paramref name="aad"/> doesn't match what the block was
    /// encrypted with, or the ciphertext/tag was tampered with after encryption.
    /// </summary>
    public static byte[] Decrypt(byte[] key, byte[] ciphertextWithTag, byte[] iv, byte[] aad)
    {
        if (ciphertextWithTag.Length < CryptoConstants.TagSize)
            throw new CryptographicException("Ciphertext too short to contain a GCM tag.");

        int ciphertextLen = ciphertextWithTag.Length - CryptoConstants.TagSize;
        byte[] ciphertext = ciphertextWithTag.AsSpan(0, ciphertextLen).ToArray();
        byte[] tag = ciphertextWithTag.AsSpan(ciphertextLen).ToArray();
        byte[] plaintext = new byte[ciphertextLen];

        using var aes = new AesGcm(key, CryptoConstants.TagSize);
        aes.Decrypt(iv, ciphertext, tag, plaintext, aad);

        return plaintext;
    }
}

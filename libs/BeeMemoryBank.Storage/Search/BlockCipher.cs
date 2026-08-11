using System.Security.Cryptography;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// AES-256-GCM encrypt/decrypt for one arbitrary-length segment block, keyed by the index key
/// (see <see cref="EncryptedSegmentStore"/>).
///
/// <para>
/// <b>Why this exists instead of calling <see cref="DekManager.WrapDek"/>/<see cref="DekManager.UnwrapDek"/>
/// directly</b> (as this WP's index-key wrapping does, and as
/// <c>BeeMemoryBank.Core.Embeddings.ProjectionMatrix</c>'s own doc comment claims it does for its
/// matrix bytes): <c>DekManager.UnwrapDek</c>'s current implementation dispatches on the wrapped
/// blob's exact byte LENGTH (48 bytes for its legacy v0 framing, 49 for v1) to decide how to frame
/// the AES-GCM call -- hardening added to eliminate ambiguity between the two wire formats. That
/// dispatch is correct for its only currently-tested use, wrapping exactly-32-byte secrets
/// (<c>ArticleService</c>/<c>MediaService</c>/<c>CommentService</c>'s per-entity DEKs, and this
/// WP's own 32-byte index key -- a 32-byte plaintext always wraps to exactly 49 bytes) -- but it
/// makes <c>DekManager.UnwrapDek</c> unable to unwrap a payload of any OTHER plaintext length.
/// Verified empirically while building this WP: a 64 KiB block wraps fine via <c>WrapDek</c>
/// (which has no length restriction), but <c>UnwrapDek</c> then throws
/// <c>CryptographicException("Invalid wrapped DEK length...")</c> unconditionally, because the
/// wrapped length is neither 48 nor 49. The same would be true of
/// <c>ProjectionMatrix.Unwrap</c> for any real (e.g. 384-dim, ~590 KB) matrix -- apparently
/// untested in this codebase today (no test exercises <c>ProjectionMatrix.Unwrap</c> with a
/// real-size matrix) -- and fixing that is out of this WP's scope: it lives in
/// <c>libs/BeeMemoryBank.Crypto/</c>, which this WP must not modify.
/// </para>
///
/// <para>
/// Given that, block-sized (~64 KiB) segment data cannot be routed through
/// <c>DekManager</c> at all. This class is the minimal, unavoidable workaround: the exact same
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

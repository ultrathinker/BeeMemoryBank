using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Optional SECOND encryption layer for individual articles ("protected articles").
///
/// The body plaintext is wrapped with a key derived solely from a per-article passphrase
/// (Argon2id) — independent of the master DEK. The resulting self-describing string is then stored
/// and encrypted by the normal article-DEK pipeline (the outer layer). So at rest the body is
/// double-encrypted: OUTER = account/master-DEK, INNER = this passphrase layer.
///
/// Why the inner key is NOT mixed with the master DEK:
///  - Two-factor-at-rest (a stolen DB needs the account password AND the passphrase) is already
///    guaranteed by the NESTING: an attacker must peel the outer master-DEK layer before they can
///    even see this inner ciphertext.
///  - Master-DEK rotation re-wraps article DEKs but never touches the body ciphertext, so an
///    inner key bound to the master DEK would become undecryptable after any rotation (no passphrase
///    is available at rotation time). An independent passphrase key survives rotation untouched.
///
/// Format (textual): "BMBENC1:" + Base64(payload), where payload =
///   [1]  version (0x01)
///   [4]  argon memory     (int32, little-endian)
///   [4]  argon iterations (int32, little-endian)
///   [4]  argon parallelism(int32, little-endian)
///   [1]  salt length
///   [..] salt
///   [1]  iv length
///   [..] iv
///   [..] ciphertext || GCM tag
/// KDF parameters are embedded so changing the global Argon2id defaults can never strand existing
/// protected articles. AAD binds the blob to this purpose so it can't be relocated into another role.
/// </summary>
public static class ProtectedContentCodec
{
    public const string Prefix = "BMBENC1:";
    private const byte FormatVersion = 0x01;
    private static readonly byte[] Aad = "BMBENC1"u8.ToArray();

    /// <summary>
    /// True if <paramref name="content"/> is a real protected blob. Beyond the prefix it
    /// structurally validates the payload (base64, version byte, plausible salt/iv/tag lengths) so a
    /// user whose markdown merely starts with "BMBENC1:" is NOT mis-flagged as protected (which would
    /// otherwise lock them out of their own plaintext article). Never throws.
    /// </summary>
    public static bool IsProtected(string? content)
    {
        if (content == null || !content.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            var payload = Convert.FromBase64String(content[Prefix.Length..]);
            var span = payload.AsSpan();
            int pos = 0;
            if (ReadByte(span, ref pos) != FormatVersion) return false;
            _ = ReadInt32(span, ref pos); // memory
            _ = ReadInt32(span, ref pos); // iterations
            _ = ReadInt32(span, ref pos); // parallelism
            int saltLen = ReadByte(span, ref pos);
            ReadBytes(span, ref pos, saltLen);
            int ivLen = ReadByte(span, ref pos);
            ReadBytes(span, ref pos, ivLen);
            // Remaining must hold at least a GCM tag.
            return span.Length - pos >= CryptoConstants.TagSize;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Wrap plaintext under a passphrase-derived key, producing the "BMBENC1:" blob.</summary>
    public static string Wrap(string plaintext, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase cannot be empty.", nameof(passphrase));

        var salt = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var key = KeyDerivation.DeriveKek(passphrase, salt);
        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                var (ciphertextWithTag, iv) = AesGcmHelper.Encrypt(key, plaintextBytes, Aad);

                using var ms = new MemoryStream();
                ms.WriteByte(FormatVersion);
                WriteInt32(ms, CryptoConstants.DefaultArgonMemory);
                WriteInt32(ms, CryptoConstants.DefaultArgonIterations);
                WriteInt32(ms, CryptoConstants.DefaultArgonParallelism);
                ms.WriteByte((byte)salt.Length);
                ms.Write(salt);
                ms.WriteByte((byte)iv.Length);
                ms.Write(iv);
                ms.Write(ciphertextWithTag);

                return Prefix + Convert.ToBase64String(ms.ToArray());
            }
            finally
            {
                Array.Clear(plaintextBytes);
            }
        }
        finally
        {
            Array.Clear(key);
        }
    }

    /// <summary>
    /// Unwrap a "BMBENC1:" blob with the passphrase. Throws <see cref="CryptographicException"/>
    /// (GCM tag mismatch) when the passphrase is wrong — callers map that to "wrong password".
    /// </summary>
    public static string Unwrap(string protectedContent, string passphrase)
    {
        if (!IsProtected(protectedContent))
            throw new ArgumentException("Content is not a protected blob.", nameof(protectedContent));

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedContent[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Malformed protected blob (base64).", ex);
        }

        var span = payload.AsSpan();
        int pos = 0;
        byte version = ReadByte(span, ref pos);
        if (version != FormatVersion)
            throw new CryptographicException($"Unsupported protected blob version: {version}.");

        int memory = ReadInt32(span, ref pos);
        int iterations = ReadInt32(span, ref pos);
        int parallelism = ReadInt32(span, ref pos);

        // SECURITY (fixed finding M4): these three numbers came from inside the blob, which is
        // attacker-controlled — anyone with write access to a folder (a restricted agent key
        // included) can save an article whose body is a hand-crafted "BMBENC1:" blob. Without a
        // bound, a value like memory = int.MaxValue asks Argon2id to allocate multiple terabytes
        // the instant a human later enters the correct passphrase — an easy way to OOM-kill the
        // whole node from a single malicious article, with no attacker-side authentication beyond
        // whatever wrote the article in the first place. Mirrors the exact bounds
        // SessionService.UnlockCoreAsync already enforces on key-slot KDF params, so the two
        // "attacker might control these numbers" call sites in the codebase agree on one policy.
        const int MinArgonMemory = 32768; // 32 MiB
        const int MinArgonIterations = 2;
        if (memory < MinArgonMemory || iterations < MinArgonIterations)
            throw new CryptographicException(
                $"Protected blob has weakened KDF params (memory={memory}, iterations={iterations}); refusing to unwrap.");

        const int MaxArgonMemory = 1_048_576;
        const int MaxArgonIterations = 20;
        const int MaxArgonParallelism = 16;
        if (memory > MaxArgonMemory || iterations > MaxArgonIterations || parallelism > MaxArgonParallelism)
            throw new CryptographicException(
                $"Protected blob has unreasonable KDF params (memory={memory}, iterations={iterations}, parallelism={parallelism}); refusing to unwrap.");

        int saltLen = ReadByte(span, ref pos);
        var salt = ReadBytes(span, ref pos, saltLen);
        int ivLen = ReadByte(span, ref pos);
        var iv = ReadBytes(span, ref pos, ivLen);
        var ciphertextWithTag = span[pos..].ToArray();

        var key = KeyDerivation.DeriveKek(passphrase, salt, memory, iterations, parallelism);
        try
        {
            var plaintextBytes = AesGcmHelper.Decrypt(key, ciphertextWithTag, iv, Aad);
            try
            {
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            finally
            {
                Array.Clear(plaintextBytes);
            }
        }
        finally
        {
            Array.Clear(key);
        }
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);
    }

    private static byte ReadByte(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 1 > span.Length) throw new CryptographicException("Truncated protected blob.");
        return span[pos++];
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 4 > span.Length) throw new CryptographicException("Truncated protected blob.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;
        return value;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> span, ref int pos, int len)
    {
        if (len < 0 || pos + len > span.Length) throw new CryptographicException("Truncated protected blob.");
        var result = span.Slice(pos, len).ToArray();
        pos += len;
        return result;
    }
}

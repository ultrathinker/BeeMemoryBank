using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// The BMBDB1/BMBDB2 container a snapshot carries its database in: the plain SQLite image, AES-256-GCM under a key derived from the
/// master DEK (docs/design/step-b-spec.md 8.3, 8.4). The format is the one <c>SnapshotService</c> has always written and every 2.5 and 2.6
/// reader understands, byte for byte: <c>"BMBDB2" || salt(16) || iv(12) || tag(16) || ciphertext</c>, key = HKDF-SHA256(master DEK, salt,
/// info "bmb-snap-db-v2"), AAD "bmb-snap-db-v2"; BMBDB1 (read only here) is <c>"BMBDB1" || iv || tag || ciphertext</c> under the master DEK
/// itself with AAD "bmb-snap-db-v1". On a sealed node the image comes from a stream (<see cref="PlainImageReader"/>), never from a plain
/// file; the whole image is held in memory, as it always was, so the 2 GiB limit of the format stays.
/// </summary>
public static class Bmbdb2Container
{
    public const string MagicV1 = "BMBDB1";
    public const string MagicV2 = "BMBDB2";
    public const int OverheadV1 = 6 + 12 + 16;
    public const int OverheadV2 = 6 + 16 + 12 + 16;

    /// <summary>The largest image the container can carry (<c>SnapshotService.MaxEncryptableDbSize</c>).</summary>
    public const long MaxImageBytes = 2L * 1024 * 1024 * 1024;

    private static readonly byte[] AadV1 = "bmb-snap-db-v1"u8.ToArray();
    private static readonly byte[] AadV2 = "bmb-snap-db-v2"u8.ToArray();

    /// <summary>True when <paramref name="head"/> (at least six bytes) starts a BMBDB1 or BMBDB2 container.</summary>
    public static bool IsContainer(ReadOnlySpan<byte> head) =>
        head.Length >= 6 && (head[..6].SequenceEqual("BMBDB2"u8) || head[..6].SequenceEqual("BMBDB1"u8));

    /// <summary>Reads <paramref name="length"/> bytes of a plain image and returns its BMBDB2 container. The master DEK is the caller's; nothing of it is kept.</summary>
    /// <exception cref="InvalidOperationException">The image is larger than <see cref="MaxImageBytes"/>.</exception>
    public static async Task<byte[]> EncryptAsync(Stream plainImage, long length, ReadOnlyMemory<byte> masterDek, CancellationToken ct = default)
    {
        if (length > MaxImageBytes)
            throw new InvalidOperationException($"Database is {length / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit.");
        if (masterDek.Length != 32) throw new ArgumentException("A master DEK is exactly 32 bytes.", nameof(masterDek));

        var image = new byte[length];
        try
        {
            await plainImage.ReadExactlyAsync(image, ct).ConfigureAwait(false);

            var salt = RandomNumberGenerator.GetBytes(16);
            var key = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek.Span, key, salt, AadV2);
            try
            {
                var container = new byte[OverheadV2 + image.Length];
                var iv = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                using (var gcm = new AesGcm(key, 16))
                    gcm.Encrypt(iv, image, container.AsSpan(OverheadV2), tag, AadV2);
                Encoding.ASCII.GetBytes(MagicV2).CopyTo(container, 0);
                salt.CopyTo(container, 6);
                iv.CopyTo(container, 22);
                tag.CopyTo(container, 34);
                return container;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    /// <summary>
    /// Decrypts a BMBDB1 or BMBDB2 container to its plain image, in memory. The caller clears the result. An authentication failure is a
    /// <see cref="CryptographicException"/> (a wrong DEK, a damaged container).
    /// </summary>
    public static async Task<byte[]> DecryptAsync(Stream container, long length, ReadOnlyMemory<byte> masterDek, CancellationToken ct = default)
    {
        if (length > MaxImageBytes + OverheadV2)
            throw new InvalidOperationException($"Database is {length / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit.");
        var blob = new byte[length];
        try
        {
            await container.ReadExactlyAsync(blob, ct).ConfigureAwait(false);
            if (blob.Length >= OverheadV2 && blob.AsSpan(0, 6).SequenceEqual("BMBDB2"u8))
            {
                var salt = blob.AsSpan(6, 16);
                var iv = blob.AsSpan(22, 12);
                var tag = blob.AsSpan(34, 16);
                var key = new byte[32];
                HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek.Span, key, salt, AadV2);
                try
                {
                    var plain = new byte[blob.Length - OverheadV2];
                    using var gcm = new AesGcm(key, 16);
                    gcm.Decrypt(iv, blob.AsSpan(OverheadV2), tag, plain, AadV2);
                    return plain;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            if (blob.Length >= OverheadV1 && blob.AsSpan(0, 6).SequenceEqual("BMBDB1"u8))
            {
                var plain = new byte[blob.Length - OverheadV1];
                using var gcm = new AesGcm(masterDek.Span, 16);
                gcm.Decrypt(blob.AsSpan(6, 12), blob.AsSpan(OverheadV1), blob.AsSpan(18, 16), plain, AadV1);
                return plain;
            }
            throw new InvalidDataException("This is not a BMBDB1 or BMBDB2 container.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }
}

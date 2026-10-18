using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// The BMBDB1/BMBDB2 container a snapshot carries its database in: the plain SQLite image, AES-256-GCM under a key derived from the
/// master DEK (docs/design/step-b-spec.md 8.3, 8.4). The format is the one <c>SnapshotService</c> has always written and every 2.5 and 2.6
/// reader understands, byte for byte: <c>"BMBDB2" || salt(16) || iv(12) || tag(16) || ciphertext</c>, key = HKDF-SHA256(master DEK, salt,
/// info "bmb-snap-db-v2"), AAD "bmb-snap-db-v2"; BMBDB1 (read only here) is <c>"BMBDB1" || iv || tag || ciphertext</c> under the master DEK
/// itself with AAD "bmb-snap-db-v1". On a sealed node the image comes from a stream (<see cref="PlainImageReader"/>), never from a plain
/// file. The container is one AES-GCM message, so the whole image is held in memory and the 2 GiB limit of the format stays; but as ONE buffer
/// (BMB-304): the image is read into the place its ciphertext takes and encrypted where it lies, a container is opened in place the same way, and
/// both ask <see cref="MemoryBudget"/> first, so that a database that cannot fit is refused with the memory limit that would be enough instead of
/// ending in an <see cref="OutOfMemoryException"/>.
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

    /// <summary>
    /// What a <c>VACUUM INTO</c> of the open database will hold: its live pages, not the free ones (the file over-counts by every page a big delete left on the
    /// free list, and a refusal that is not true stops a snapshot that would have been made). The file length when the pragmas cannot be read.
    /// </summary>
    public static long LiveImageBytes(System.Data.IDbConnection connection, long fileLength)
    {
        try
        {
            long Pragma(string name)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA " + name;
                return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            var live = (Pragma("page_count") - Pragma("freelist_count")) * Pragma("page_size");
            return live > 0 ? Math.Min(live, fileLength) : fileLength;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidCastException or FormatException or OverflowException)
        {
            return fileLength;
        }
    }

    /// <summary>True when <paramref name="head"/> (at least six bytes) starts a BMBDB1 or BMBDB2 container.</summary>
    public static bool IsContainer(ReadOnlySpan<byte> head) =>
        head.Length >= 6 && (head[..6].SequenceEqual("BMBDB2"u8) || head[..6].SequenceEqual("BMBDB1"u8));

    /// <summary>
    /// Reads <paramref name="length"/> bytes of a plain image and returns its BMBDB2 container. The master DEK is the caller's; nothing of it is kept.
    /// The peak is the container itself: one array of <see cref="OverheadV2"/> + <paramref name="length"/> bytes.
    /// </summary>
    /// <param name="freeMemoryBytes">The room to measure against instead of asking the runtime (a test's ceiling); null asks the runtime.</param>
    /// <exception cref="InvalidOperationException">The image is larger than <see cref="MaxImageBytes"/>.</exception>
    /// <exception cref="NotEnoughMemoryException">The process cannot hold the container; the message names the limit that would be enough.</exception>
    public static async Task<byte[]> EncryptAsync(
        Stream plainImage, long length, ReadOnlyMemory<byte> masterDek, CancellationToken ct = default, long? freeMemoryBytes = null)
    {
        if (length > MaxImageBytes || length + OverheadV2 > Array.MaxLength)
            throw new InvalidOperationException($"Database is {length / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit.");
        if (masterDek.Length != 32) throw new ArgumentException("A master DEK is exactly 32 bytes.", nameof(masterDek));
        MemoryBudget.RequireFree(length + OverheadV2 + MemoryBudget.Slack, "Packing the database into a snapshot", freeMemoryBytes);

        var container = MemoryBudget.Allocate(OverheadV2 + length, "Packing the database into a snapshot");
        var done = false;
        try
        {
            var body = container.AsMemory(OverheadV2);
            await plainImage.ReadExactlyAsync(body, ct).ConfigureAwait(false);

            var salt = RandomNumberGenerator.GetBytes(16);
            var iv = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var key = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek.Span, key, salt, AadV2);
            try
            {
                // In place: AES-GCM takes a plaintext and a ciphertext that are exactly the same bytes, so no second copy of the image exists.
                using var gcm = new AesGcm(key, 16);
                gcm.Encrypt(iv, body.Span, body.Span, tag, AadV2);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
            Encoding.ASCII.GetBytes(MagicV2).CopyTo(container, 0);
            salt.CopyTo(container, 6);
            iv.CopyTo(container, 22);
            tag.CopyTo(container, 34);
            done = true;
            return container;
        }
        finally
        {
            // Whatever was read and not encrypted yet is a plain image.
            if (!done) CryptographicOperations.ZeroMemory(container);
        }
    }

    /// <summary>
    /// Opens a BMBDB1 or BMBDB2 container in place: the image is decrypted where the container was read, so the peak is one buffer of
    /// <paramref name="length"/> bytes. The caller disposes the result, which clears the image. An authentication failure is a
    /// <see cref="CryptographicException"/> (a wrong DEK, a damaged container).
    /// </summary>
    /// <param name="freeMemoryBytes">The room to measure against instead of asking the runtime (a test's ceiling); null asks the runtime.</param>
    /// <exception cref="NotEnoughMemoryException">The process cannot hold the container; the message names the limit that would be enough.</exception>
    public static async Task<DecryptedImage> OpenAsync(
        Stream container, long length, ReadOnlyMemory<byte> masterDek, CancellationToken ct = default, long? freeMemoryBytes = null)
    {
        if (length > MaxImageBytes + OverheadV2 || length > Array.MaxLength)
            throw new InvalidOperationException($"Database is {length / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit.");
        MemoryBudget.RequireFree(length + MemoryBudget.Slack, "Opening the database of a snapshot", freeMemoryBytes);

        var blob = MemoryBudget.Allocate(length, "Opening the database of a snapshot");
        var done = false;
        try
        {
            await container.ReadExactlyAsync(blob, ct).ConfigureAwait(false);
            if (blob.Length >= OverheadV2 && blob.AsSpan(0, 6).SequenceEqual("BMBDB2"u8))
            {
                var key = new byte[32];
                HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek.Span, key, blob.AsSpan(6, 16), AadV2);
                try
                {
                    using var gcm = new AesGcm(key, 16);
                    var body = blob.AsSpan(OverheadV2);
                    gcm.Decrypt(blob.AsSpan(22, 12), body, blob.AsSpan(34, 16), body, AadV2);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
                done = true;
                return new DecryptedImage(blob, OverheadV2, blob.Length - OverheadV2);
            }
            if (blob.Length >= OverheadV1 && blob.AsSpan(0, 6).SequenceEqual("BMBDB1"u8))
            {
                using var gcm = new AesGcm(masterDek.Span, 16);
                var body = blob.AsSpan(OverheadV1);
                gcm.Decrypt(blob.AsSpan(6, 12), body, blob.AsSpan(18, 16), body, AadV1);
                done = true;
                return new DecryptedImage(blob, OverheadV1, blob.Length - OverheadV1);
            }
            throw new InvalidDataException("This is not a BMBDB1 or BMBDB2 container.");
        }
        finally
        {
            if (!done) CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>
    /// Decrypts a BMBDB1 or BMBDB2 container to its plain image as an array of its own (a second copy: for a small image, a test, a caller that needs an
    /// array; a database goes through <see cref="OpenAsync"/>). The caller clears the result.
    /// </summary>
    public static async Task<byte[]> DecryptAsync(
        Stream container, long length, ReadOnlyMemory<byte> masterDek, CancellationToken ct = default, long? freeMemoryBytes = null)
    {
        using var image = await OpenAsync(container, length, masterDek, ct, freeMemoryBytes).ConfigureAwait(false);
        // The second whole image of this method goes through the same budget as the first: asked for, and taken as one buffer that refuses in words.
        MemoryBudget.RequireFree(image.Length, "Copying the database of a snapshot", freeMemoryBytes);
        var copy = MemoryBudget.Allocate(image.Length, "Copying the database of a snapshot");
        image.Span.CopyTo(copy);
        return copy;
    }
}

/// <summary>
/// The plain image of an opened container, where it was decrypted (<see cref="Bmbdb2Container.OpenAsync"/>): a window on the buffer the container was
/// read into. Disposing it clears the whole buffer.
/// </summary>
public sealed class DecryptedImage(byte[] buffer, int offset, int length) : IDisposable
{
    public int Length { get; } = length;

    public ReadOnlySpan<byte> Span => buffer.AsSpan(offset, Length);

    /// <summary>The buffer the image lies in and where: for the importer, which works on it in place instead of copying it (<see cref="PlainImageImporter"/>).</summary>
    internal ArraySegment<byte> Segment => new(buffer, offset, Length);

    /// <summary>The image as memory (a window on the buffer, no copy): for a writer that puts it on the disk in blocks.</summary>
    public ReadOnlyMemory<byte> Memory => buffer.AsMemory(offset, Length);

    /// <summary>A read-only stream over the image; it does not copy it.</summary>
    public Stream OpenRead() => new MemoryStream(buffer, offset, Length, writable: false);

    public void Dispose() => CryptographicOperations.ZeroMemory(buffer);
}

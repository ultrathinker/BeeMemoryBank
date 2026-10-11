using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>How <see cref="PlainImageImporter"/> turned an image into a sealed file.</summary>
public enum SealedImportMethod
{
    /// <summary>Page by page through <see cref="SealedFileWriter"/>: bounded memory (an image of 4096-byte pages with 80 or more reserved bytes).</summary>
    Streamed,
    /// <summary>The whole image in memory, <c>sqlite3_deserialize</c> and <c>VACUUM INTO</c> under the page key (an older image with no page reserve).</summary>
    Deserialized,
}

/// <summary>The image needs the memory path and is larger than the memory cap (spec 8.2). Fails closed; nothing is written.</summary>
public sealed class SealedImportTooLargeException()
    : InvalidOperationException("This archive was made by an older version and is too large to import into an encrypted database here. " +
                                "Restore it on a node that is not encrypted, or have an updated node make it again.");

/// <summary>
/// Turns the plain SQLite image a container carries (BMBDB1/2 snapshots, BMBCH1 baselines, join archives, packages) into a sealed file
/// under a page key, without ever writing a plain file (docs/design/step-b-spec.md 8.1, 8.2). The only file that appears is the keyed
/// target.
/// </summary>
public static class PlainImageImporter
{
    private const long DesktopMemoryCap = 1L << 30;
    private const long PhoneMemoryCap = 256L << 20;
    private const int HeaderSize = 100;
    private const int DeserializeReadOnly = 4; // SQLITE_DESERIALIZE_READONLY

    /// <summary>1 GiB on desktops and servers, 256 MiB on phones; <c>BMB_SEALED_IMPORT_MEMORY_MB</c> overrides (spec 8.2).</summary>
    public static long DefaultMemoryCap()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("BMB_SEALED_IMPORT_MEMORY_MB"), out var mb) && mb > 0)
            return mb << 20;
        return OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? PhoneMemoryCap : DesktopMemoryCap;
    }

    /// <summary>
    /// Writes <paramref name="image"/> (<paramref name="length"/> bytes of a plain SQLite database) to <paramref name="target"/>, a file that
    /// must not exist, sealed under <paramref name="pageKey"/>. An image of 4096-byte pages with at least 80 reserved bytes (every image a
    /// 2.6 reader made, spec 8.3) is streamed; any other image (from a 2.5 node or a plain 2.6 node: no reserve) is held in memory when it
    /// is no larger than <paramref name="memoryCap"/>, else <see cref="SealedImportTooLargeException"/>. The page key is the caller's copy;
    /// the caller clears it. On any failure the target is not left behind.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a SQLite database.</exception>
    public static async Task<SealedImportMethod> ToSealedFileAsync(
        Stream image, long length, string target, byte[] pageKey, long memoryCap, CancellationToken ct = default)
    {
        if (length < HeaderSize) throw new InvalidDataException("The image is too short to be a SQLite database.");
        var head = new byte[HeaderSize];
        try { await image.ReadExactlyAsync(head, ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new InvalidDataException("The image is shorter than its stated length."); }
        if (!head.AsSpan(0, 16).SequenceEqual(SqlCipherPageFormat.SqliteMagic))
            throw new InvalidDataException("The image is not a SQLite database.");

        var pageSize = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(16, 2));
        var reserved = head[20];
        if (pageSize == SqlCipherPageFormat.PageSize && reserved >= SealedFileWriter.MinReservedBytes && length % SqlCipherPageFormat.PageSize == 0)
        {
            await SealedFileWriter.WriteAsync(new PrefixedStream(head, image), length, target, pageKey, ct).ConfigureAwait(false);
            return SealedImportMethod.Streamed;
        }

        // A sealed file is whole 4096-byte pages (spec 2.5): an image with another page size cannot be rewritten to one by VACUUM INTO
        // (it keeps the source's page size). Every vault the product ever made has 4096; anything else fails closed.
        if (pageSize != SqlCipherPageFormat.PageSize)
            throw new InvalidDataException($"The image uses {(pageSize == 1 ? 65536 : pageSize)}-byte pages; only 4096-byte pages can be imported into an encrypted database.");
        if (length > memoryCap) throw new SealedImportTooLargeException();
        await DeserializeAndVacuumAsync(head, image, length, target, pageKey, ct).ConfigureAwait(false);
        return SealedImportMethod.Deserialized;
    }

    private static async Task DeserializeAndVacuumAsync(byte[] head, Stream image, long length, string target, byte[] pageKey, CancellationToken ct)
    {
        var memory = Marshal.AllocHGlobal(checked((nint)length));
        try
        {
            // Read the image straight into native memory in blocks: the managed heap never holds more than one block of it.
            var block = new byte[1 << 20];
            head.CopyTo(block, 0);
            var filled = head.Length;
            long written = 0;
            while (written < length)
            {
                var want = (int)Math.Min(block.Length, length - written);
                while (filled < want)
                {
                    var n = await image.ReadAsync(block.AsMemory(filled, want - filled), ct).ConfigureAwait(false);
                    if (n == 0) throw new InvalidDataException("The image is shorter than its stated length.");
                    filled += n;
                }
                Marshal.Copy(block, 0, memory + (nint)written, want);
                written += want;
                filled = 0;
            }
            Array.Clear(block);

            // An image cannot be in WAL mode (spec Appendix C): bytes 18 and 19 say rollback journal.
            Marshal.WriteByte(memory, 18, 1);
            Marshal.WriteByte(memory, 19, 1);

            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var rc = raw.sqlite3_deserialize(connection.Handle, "main", memory, length, length, DeserializeReadOnly);
            if (rc != raw.SQLITE_OK) throw new InvalidDataException($"The image could not be loaded (SQLite result {rc}).");
            try
            {
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO " + SqlCipherScheme.VacuumIntoTarget(target, pageKey);
                vacuum.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 26 or 11) // not a database / malformed
            {
                TryDelete(target);
                throw new InvalidDataException("The image is not a SQLite database.");
            }
            catch
            {
                TryDelete(target);
                throw;
            }
        }
        finally
        {
            // The image is plain data: zeroed before it goes back to the allocator.
            var zeros = new byte[1 << 20];
            for (long offset = 0; offset < length; offset += zeros.Length)
                Marshal.Copy(zeros, 0, memory + (nint)offset, (int)Math.Min(zeros.Length, length - offset));
            Marshal.FreeHGlobal(memory);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A stream that first yields bytes already read, then the rest of another stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _prefixPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (_prefixPosition < prefix.Length)
            {
                var n = Math.Min(destination.Length, prefix.Length - _prefixPosition);
                prefix.AsSpan(_prefixPosition, n).CopyTo(destination);
                _prefixPosition += n;
                return n;
            }
            return rest.Read(destination);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < prefix.Length)
            {
                var n = Math.Min(destination.Length, prefix.Length - _prefixPosition);
                prefix.AsSpan(_prefixPosition, n).CopyTo(destination.Span);
                _prefixPosition += n;
                return n;
            }
            return await rest.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}

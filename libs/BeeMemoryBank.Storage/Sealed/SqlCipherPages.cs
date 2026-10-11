using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>
/// The SQLCipher 4 page format as SQLite3 Multiple Ciphers writes it with <c>cipher=sqlcipher&amp;legacy=4</c> and a raw 256-bit key
/// (docs/design/step-b-spec.md Appendix C): 4096-byte pages, the last 80 bytes of each page are a 16-byte IV and a 64-byte
/// HMAC-SHA512, page 1 starts with a 16-byte random salt instead of the SQLite header, the pages are AES-256-CBC without padding.
/// </summary>
internal static class SqlCipherPageFormat
{
    public const int PageSize = SqlCipherScheme.PageSize;
    public const int Reserved = SqlCipherScheme.ReservedBytes;
    public const int IvSize = 16;
    public const int HmacSize = 64;
    public const int SaltSize = 16;
    public const int ContentEnd = PageSize - Reserved; // 4016
    public const int KeySize = 32;

    public static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    /// <summary>The key of the page HMACs: PBKDF2-HMAC-SHA512(page key, salt XOR 0x3a, 2 iterations).</summary>
    public static byte[] HmacKey(ReadOnlySpan<byte> pageKey, ReadOnlySpan<byte> salt)
    {
        Span<byte> xored = stackalloc byte[SaltSize];
        for (var i = 0; i < SaltSize; i++) xored[i] = (byte)(salt[i] ^ 0x3a);
        return Rfc2898DeriveBytes.Pbkdf2(pageKey, xored, 2, HashAlgorithmName.SHA512, KeySize);
    }

    /// <summary>First byte of the encrypted part of a page: 16 on page 1 (the salt takes the header's place), else 0.</summary>
    public static int ContentStart(int pageNumber) => pageNumber == 1 ? SaltSize : 0;

    public static bool IsAllZero(ReadOnlySpan<byte> page) => !page.ContainsAnyExcept((byte)0);
}

/// <summary>
/// Reads a closed, checkpointed SQLCipher 4 file under its page key and yields its plain image page by page (spec 8.2): every page's
/// HMAC is verified (a mismatch throws <see cref="InvalidDataException"/>), the page is decrypted, page 1's first 16 bytes become
/// <c>SQLite format 3\0</c>, the 80 reserved bytes of every page are zeroed, and header bytes 18 and 19 are set to 1 (a rollback-journal
/// image: an image cannot be in WAL mode). The image has the length of the file and is a valid SQLite file with 80 reserved bytes per
/// page, which stock SQLite and SQLite3MC read. One page is held at a time; the page key is copied and the copies are zeroed on dispose.
/// </summary>
public sealed class PlainImageReader : Stream
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly byte[] _aesKey;
    private readonly byte[] _hmacKey;
    private readonly Aes _aes;
    private readonly IncrementalHash _hmac;
    private readonly byte[] _encrypted = new byte[SqlCipherPageFormat.PageSize];
    private readonly byte[] _plain = new byte[SqlCipherPageFormat.PageSize];
    private readonly long _length;
    private long _position;
    private int _cachedPage; // 1-based page number held in _plain, 0 = none
    private bool _disposed;

    /// <param name="encrypted">The sealed file (readable; seekable if the image is to be seekable).</param>
    /// <param name="pageKey">A copy is taken; the caller clears its own.</param>
    public PlainImageReader(Stream encrypted, ReadOnlySpan<byte> pageKey, bool leaveOpen = false)
    {
        if (pageKey.Length != SqlCipherPageFormat.KeySize) throw new ArgumentException("A page key is exactly 32 bytes.", nameof(pageKey));
        if (!encrypted.CanRead || !encrypted.CanSeek) throw new ArgumentException("The sealed file must be a readable, seekable stream.", nameof(encrypted));
        _source = encrypted;
        _leaveOpen = leaveOpen;
        _length = encrypted.Length;
        if (_length == 0 || _length % SqlCipherPageFormat.PageSize != 0)
        {
            if (!leaveOpen) encrypted.Dispose();
            throw new InvalidDataException("This is not a whole number of 4096-byte pages: not a sealed database file.");
        }

        // The salt is the first 16 bytes of the file; the HMAC key depends on it.
        Span<byte> salt = stackalloc byte[SqlCipherPageFormat.SaltSize];
        encrypted.Position = 0;
        encrypted.ReadExactly(salt);
        encrypted.Position = 0;
        _aesKey = pageKey.ToArray();
        _hmacKey = SqlCipherPageFormat.HmacKey(pageKey, salt);
        _aes = Aes.Create();
        _aes.Key = _aesKey;
        _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, _hmacKey);
        PageCount = (int)(_length / SqlCipherPageFormat.PageSize);
    }

    /// <summary>Opens the file (shared for reading) and reads it under <paramref name="pageKey"/>.</summary>
    public static PlainImageReader Open(string path, ReadOnlySpan<byte> pageKey) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan), pageKey);

    public int PageCount { get; }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (!destination.IsEmpty && _position < _length)
        {
            var page = (int)(_position / SqlCipherPageFormat.PageSize) + 1;
            LoadPage(page);
            var inPage = (int)(_position % SqlCipherPageFormat.PageSize);
            var n = Math.Min(destination.Length, SqlCipherPageFormat.PageSize - inPage);
            _plain.AsSpan(inPage, n).CopyTo(destination);
            destination = destination[n..];
            _position += n;
            total += n;
        }
        return total;
    }

    /// <summary>Decrypts a page into the one-page cache after verifying its HMAC.</summary>
    private void LoadPage(int page)
    {
        if (_cachedPage == page) return;
        _cachedPage = 0;
        _source.Position = (long)(page - 1) * SqlCipherPageFormat.PageSize;
        _source.ReadExactly(_encrypted);

        var start = SqlCipherPageFormat.ContentStart(page);
        var content = _encrypted.AsSpan(start, SqlCipherPageFormat.ContentEnd - start);
        var iv = _encrypted.AsSpan(SqlCipherPageFormat.ContentEnd, SqlCipherPageFormat.IvSize);
        var mac = _encrypted.AsSpan(SqlCipherPageFormat.ContentEnd + SqlCipherPageFormat.IvSize, SqlCipherPageFormat.HmacSize);

        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)page);
        _hmac.AppendData(content);
        _hmac.AppendData(iv);
        _hmac.AppendData(number);
        Span<byte> expected = stackalloc byte[SqlCipherPageFormat.HmacSize];
        _hmac.GetHashAndReset(expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
            throw new InvalidDataException($"Page {page} of the encrypted database failed its integrity check: wrong key or a damaged file.");

        Array.Clear(_plain);
        _aes.DecryptCbc(content, iv, _plain.AsSpan(start, content.Length), PaddingMode.None);
        if (page == 1)
        {
            SqlCipherPageFormat.SqliteMagic.CopyTo(_plain);
            _plain[18] = 1;
            _plain[19] = 1;
        }
        _cachedPage = page;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            CryptographicOperations.ZeroMemory(_aesKey);
            CryptographicOperations.ZeroMemory(_hmacKey);
            CryptographicOperations.ZeroMemory(_plain);
            _aes.Dispose();
            _hmac.Dispose();
            if (!_leaveOpen) _source.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The reverse of <see cref="PlainImageReader"/> (spec 8.2): writes a plain image with a page size of 4096 and at least 80 reserved
/// bytes per page as a SQLCipher 4 file under a page key (a random salt, a random IV per page, the HMAC), one page at a time. The result
/// opens with SQLite3 Multiple Ciphers under that key. Nothing but the keyed target is ever written.
/// </summary>
public static class SealedFileWriter
{
    private const int HeaderPageSize = 4096;

    /// <summary>The smallest number of reserved bytes per page an image must have to be written without moving any content.</summary>
    public const int MinReservedBytes = SqlCipherPageFormat.Reserved;

    /// <summary>Writes <paramref name="image"/> (<paramref name="length"/> bytes) to a new file at <paramref name="targetPath"/>.</summary>
    /// <exception cref="InvalidDataException">The image is not a SQLite database of 4096-byte pages with 80 or more reserved bytes.</exception>
    public static void Write(Stream image, long length, string targetPath, ReadOnlySpan<byte> pageKey)
    {
        using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None);
        try
        {
            Write(image, length, target, pageKey);
            target.Flush(true);
        }
        catch
        {
            target.Dispose();
            TryDelete(targetPath);
            throw;
        }
    }

    /// <summary>Async twin of <see cref="Write(Stream, long, string, ReadOnlySpan{byte})"/>; the key is copied before the first await.</summary>
    public static async Task WriteAsync(Stream image, long length, string targetPath, ReadOnlyMemory<byte> pageKey, CancellationToken ct = default)
    {
        var key = pageKey.ToArray();
        try
        {
            await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            try
            {
                await WriteCoreAsync(image, length, target, key, ct).ConfigureAwait(false);
                await target.FlushAsync(ct).ConfigureAwait(false);
                target.Flush(true);
            }
            catch
            {
                await target.DisposeAsync().ConfigureAwait(false);
                TryDelete(targetPath);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Write(Stream image, long length, Stream target, ReadOnlySpan<byte> pageKey)
    {
        using var session = new Session(length, pageKey);
        var page = new byte[SqlCipherPageFormat.PageSize];
        var sealedPage = new byte[SqlCipherPageFormat.PageSize];
        for (var number = 1; number <= session.PageCount; number++)
        {
            try { image.ReadExactly(page); }
            catch (EndOfStreamException) { throw new InvalidDataException("The image is shorter than its stated length."); }
            session.SealPage(number, page, sealedPage);
            target.Write(sealedPage);
        }
    }

    private static async Task WriteCoreAsync(Stream image, long length, Stream target, byte[] pageKey, CancellationToken ct)
    {
        using var session = new Session(length, pageKey);
        var page = new byte[SqlCipherPageFormat.PageSize];
        var sealedPage = new byte[SqlCipherPageFormat.PageSize];
        for (var number = 1; number <= session.PageCount; number++)
        {
            try { await image.ReadExactlyAsync(page, ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { throw new InvalidDataException("The image is shorter than its stated length."); }
            session.SealPage(number, page, sealedPage);
            await target.WriteAsync(sealedPage, ct).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Session : IDisposable
    {
        private readonly byte[] _aesKey;
        private readonly byte[] _hmacKey;
        private readonly byte[] _salt = RandomNumberGenerator.GetBytes(SqlCipherPageFormat.SaltSize);
        private readonly Aes _aes;
        private readonly IncrementalHash _hmac;

        public Session(long length, ReadOnlySpan<byte> pageKey)
        {
            if (pageKey.Length != SqlCipherPageFormat.KeySize) throw new ArgumentException("A page key is exactly 32 bytes.", nameof(pageKey));
            if (length < SqlCipherPageFormat.PageSize || length % SqlCipherPageFormat.PageSize != 0)
                throw new InvalidDataException("The image is not a whole number of 4096-byte pages.");
            PageCount = checked((int)(length / SqlCipherPageFormat.PageSize));
            _aesKey = pageKey.ToArray();
            _aes = Aes.Create();
            _aes.Key = _aesKey;
            _hmacKey = SqlCipherPageFormat.HmacKey(pageKey, _salt);
            _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, _hmacKey);
        }

        public int PageCount { get; }

        public void SealPage(int number, byte[] plain, byte[] output)
        {
            if (number == 1) CheckHeader(plain);
            var start = SqlCipherPageFormat.ContentStart(number);
            var content = plain.AsSpan(start, SqlCipherPageFormat.ContentEnd - start);
            var iv = output.AsSpan(SqlCipherPageFormat.ContentEnd, SqlCipherPageFormat.IvSize);
            RandomNumberGenerator.Fill(iv);
            Array.Clear(output, 0, SqlCipherPageFormat.ContentEnd);
            if (number == 1) _salt.CopyTo(output, 0);
            _aes.EncryptCbc(content, iv, output.AsSpan(start, content.Length), PaddingMode.None);

            Span<byte> numberBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(numberBytes, (uint)number);
            _hmac.AppendData(output.AsSpan(start, content.Length));
            _hmac.AppendData(iv);
            _hmac.AppendData(numberBytes);
            _hmac.GetHashAndReset(output.AsSpan(SqlCipherPageFormat.ContentEnd + SqlCipherPageFormat.IvSize, SqlCipherPageFormat.HmacSize));
        }

        private static void CheckHeader(byte[] page1)
        {
            if (!page1.AsSpan(0, 16).SequenceEqual(SqlCipherPageFormat.SqliteMagic))
                throw new InvalidDataException("The image does not start with the SQLite header.");
            var size = BinaryPrimitives.ReadUInt16BigEndian(page1.AsSpan(16, 2));
            if (size != HeaderPageSize)
                throw new InvalidDataException("The image has a page size other than 4096.");
            if (page1[20] < MinReservedBytes)
                throw new InvalidDataException("The image has fewer than 80 reserved bytes per page.");
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_aesKey);
            CryptographicOperations.ZeroMemory(_hmacKey);
            _aes.Dispose();
            _hmac.Dispose();
        }
    }
}

using System.IO.Compression;
using System.Text;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Raised when an uploaded archive expands past the extraction ceiling. Derives from
/// <see cref="InvalidOperationException"/> so it lands on the same "this ZIP is unusable, reject
/// the whole upload" path <see cref="BeeImportService"/> already uses for a missing manifest —
/// the API turns that into a 400 carrying <see cref="Exception.Message"/>, which is why the
/// message has to read as an explanation to the operator, not as a stack-trace fragment.
/// </summary>
public sealed class ZipExtractionLimitException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads entries out of an UNTRUSTED ZIP under a hard decompression ceiling.
///
/// <para>
/// The threat is a decompression bomb: an upload well under the endpoint's 500 MB request limit
/// whose entries expand to hundreds of gigabytes, taking the node down on <c>ms.ToArray()</c>
/// before any of the import's own size checks (MediaService's 50 MB input cap, for instance) ever
/// get to run — they all inspect a byte[] that has already been allocated.
/// </para>
///
/// <para>
/// <see cref="ZipArchiveEntry.Length"/> is NOT a usable control here: it is a field the archive
/// declares about itself. A stored (uncompressed) entry whose central-directory size field has
/// been rewritten to 1 KB still streams out every byte that is physically present, so a check
/// against the declared length passes while the read runs away. The ceiling below is therefore
/// charged against bytes as they come off the decompressor, and the read is abandoned the moment
/// it is crossed rather than after the entry has been materialised.
/// </para>
///
/// <para>
/// One instance per archive. A per-entry cap alone stops nothing — an attacker just splits the
/// payload across a thousand entries — so the running total lives on the instance and every read
/// charges it automatically. That is deliberate: there is no overload that reads an entry without
/// the aggregate being counted, so a caller cannot forget it.
/// </para>
/// </summary>
public sealed class ZipEntryReader
{
    /// <summary>Per-entry ceiling. Comfortably above MediaService's own 50 MB input cap, so it
    /// only ever rejects archives that were never importable to begin with.</summary>
    public const long DefaultMaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>Ceiling on everything read out of one archive.</summary>
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;

    private const int CopyBufferSize = 81920;

    private readonly long _maxEntryBytes;
    private readonly long _maxTotalBytes;
    private long _totalBytesRead;

    public ZipEntryReader(long maxEntryBytes = DefaultMaxEntryBytes, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        _maxEntryBytes = maxEntryBytes;
        _maxTotalBytes = maxTotalBytes;
    }

    /// <summary>Total decompressed bytes this instance has handed out (or discarded) so far.</summary>
    public long TotalBytesRead => _totalBytesRead;

    /// <summary>Reads the entry fully into memory, bounded by the ceilings.</summary>
    public async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var bounded = Open(entry);
        using var buffer = new MemoryStream();
        await bounded.CopyToAsync(buffer, CopyBufferSize, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the entry as text, bounded by the ceilings. Decoding is left to
    /// <see cref="StreamReader"/> with BOM detection so imported files keep decoding exactly as
    /// they did before the bound existed — export ZIPs are written with a UTF-8 preamble.
    /// </summary>
    public async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var bounded = Open(entry);
        using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Decompresses the entry and throws the bytes away, purely to charge them against the
    /// ceilings. Used by <see cref="PreflightAsync"/>.
    /// </summary>
    public async Task MeasureAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var bounded = Open(entry);
        await bounded.CopyToAsync(Stream.Null, CopyBufferSize, ct);
    }

    /// <summary>
    /// Proves an archive stays under the ceilings BEFORE the caller writes anything.
    ///
    /// <para>
    /// Both importers write incrementally — article by article, image by image — so enforcing the
    /// bound only at the point of use would leave a half-imported tree behind on rejection. This
    /// pass decompresses (and discards) everything the import is about to open, so "the import was
    /// refused and nothing was saved" is literally true. It costs one extra decompression of a
    /// legitimate archive; the ceilings themselves cap what a hostile one can make it do.
    /// </para>
    ///
    /// <para>Uses its own budget, so the caller's read pass starts from zero.</para>
    /// </summary>
    public static async Task PreflightAsync(IEnumerable<ZipArchiveEntry> entries, CancellationToken ct)
    {
        var probe = new ZipEntryReader();
        // An entry can legitimately reach us twice (an "attachments/pic.png" is also an indexed
        // image); charging it twice would fail an archive that is actually within budget.
        foreach (var entry in entries.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            await probe.MeasureAsync(entry, ct);
        }
    }

    private Stream Open(ZipArchiveEntry entry) => new BoundedReadStream(entry.Open(), this, entry.FullName);

    private void Charge(string entryName, ref long entryBytes, int count)
    {
        entryBytes += count;
        if (entryBytes > _maxEntryBytes)
        {
            throw new ZipExtractionLimitException(
                $"Import refused: the file '{entryName}' inside the archive expands to more than " +
                $"{Describe(_maxEntryBytes)} once decompressed. Nothing was imported. " +
                "The size a ZIP declares for its own entries can be forged, so this limit is " +
                "measured while reading.");
        }

        _totalBytesRead += count;
        if (_totalBytesRead > _maxTotalBytes)
        {
            throw new ZipExtractionLimitException(
                $"Import refused: the archive expands to more than {Describe(_maxTotalBytes)} in " +
                $"total once decompressed (the limit was reached while reading '{entryName}'). " +
                "Nothing was imported. Split the export into smaller archives and import them one " +
                "at a time.");
        }
    }

    private static string Describe(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
            : $"{bytes / (1024 * 1024)} MB";

    /// <summary>
    /// Read-only pass-through that charges every byte against the owning reader's budgets. Being a
    /// stream rather than a "read it all, then check" helper is the point: the throw happens on the
    /// chunk that crosses the line, so nothing beyond the ceiling is ever allocated, and callers
    /// keep using the same <see cref="StreamReader"/>/<see cref="Stream.CopyToAsync(Stream)"/>
    /// plumbing they used before.
    /// </summary>
    private sealed class BoundedReadStream(Stream inner, ZipEntryReader owner, string entryName) : Stream
    {
        private long _entryBytes;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0) owner.Charge(entryName, ref _entryBytes, read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            if (read > 0) owner.Charge(entryName, ref _entryBytes, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            if (read > 0) owner.Charge(entryName, ref _entryBytes, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0) owner.Charge(entryName, ref _entryBytes, read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

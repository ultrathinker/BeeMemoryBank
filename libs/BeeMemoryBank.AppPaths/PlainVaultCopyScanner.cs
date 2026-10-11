using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;

namespace BeeMemoryBank.AppPaths;

/// <summary>Which live files the scanner may leave alone (docs/design/step-b-spec.md 10.2).</summary>
public enum PlainScanMode
{
    /// <summary>Before the migration: the plain <c>beememorybank.db</c> and <c>chat.db</c> and their sidecars are about to be replaced and are allowed.</summary>
    Preflight,
    /// <summary>After it: the plain boot store and its sidecars are allowed; a plain <c>beememorybank.db</c> or <c>chat.db</c> is a finding.</summary>
    Sealed,
}

/// <summary>The kinds of <see cref="PlainCopyFinding"/> (spec 10.2).</summary>
public static class PlainCopyKinds
{
    /// <summary>A file that starts with the SQLite magic, whatever tables it has and whatever it is called.</summary>
    public const string PlainSqlite = "plain_sqlite";
    /// <summary>A WAL or rollback journal that holds at least one plain page image.</summary>
    public const string PlainJournal = "plain_journal";
    /// <summary>A gzip, tar, tar.gz or zip with a plain SQLite database as any member.</summary>
    public const string PlainArchive = "plain_archive";
    /// <summary>An archive this version cannot look into (another format, nested, encrypted, unreadable to its end, over a limit): never silently accepted.</summary>
    public const string UnverifiedArchive = "unverified_archive";
    /// <summary>A file or folder that could not be opened or listed: fail closed.</summary>
    public const string Unreadable = "unreadable";
    /// <summary>A symbolic link or reparse point; it is reported and its target is not scanned.</summary>
    public const string Link = "link";
    /// <summary>A file with the identity of a live file: another name of the live database.</summary>
    public const string SharesIdentity = "shares_identity";
    /// <summary>A live file whose link count is above one: another name exists somewhere.</summary>
    public const string LiveLinked = "live_linked";
    /// <summary>The identity of a live file could not be read on this platform: the check cannot be made, so the preflight stays blocked.</summary>
    public const string IdentityUnavailable = "identity_unavailable";

    /// <summary>The kinds the erase may overwrite and delete (spec 10.2): never a link, a second name, an unreadable file or an archive it could not read.</summary>
    public static bool CanBeErased(string kind) => kind is PlainSqlite or PlainJournal or PlainArchive;
}

/// <summary>One file the scanner will not let pass: its path relative to the data directory (forward slashes), its size and last write, why.</summary>
public sealed record PlainCopyFinding(string RelativePath, long Bytes, DateTime LastWriteUtc, string Kind, string? Detail = null);

/// <summary>The resource limits of the archive rules; the tests inject smaller ones.</summary>
public sealed record PlainScanLimits(int MaxArchiveEntries = 100_000, long MaxInflatedBytes = 16L << 30);

/// <summary>
/// Finds every file in a data directory that is a plain image of the vault, by CONTENT (docs/design/step-b-spec.md 10.2): never by name,
/// folder or schema, skipping nothing (<c>media/</c>, <c>snapshots/</c>, <c>tmp/</c> and the rest are walked like every folder). No link is
/// followed, no SQLite connection is opened, nothing is extracted to disk or changed, and a file that cannot be read is a finding (fail
/// closed). The scan does not stop at the first finding. It is the preflight blocker <c>plain_copies</c>, the erase and the final scan of
/// the migration.
/// </summary>
public static class PlainVaultCopyScanner
{
    private const int HeadSize = 512;

    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    public static IReadOnlyList<PlainCopyFinding> Scan(string dataPath, PlainScanMode mode, PlainScanLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new PlainScanLimits();
        var root = Path.GetFullPath(dataPath);
        var findings = new List<PlainCopyFinding>();
        if (!Directory.Exists(root)) return findings;

        var liveNames = LiveNames(mode);
        var liveFiles = new List<(string Relative, string Full)>();
        var identityCandidates = new List<(PlainCopyFinding Finding, string Full)>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException)
            {
                continue; // vanished while we scanned
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(new PlainCopyFinding(Relative(root, directory), 0, DateTime.UtcNow, PlainCopyKinds.Unreadable, "folder cannot be listed"));
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Relative(root, entry.FullName);
                bool isLink;
                try
                {
                    isLink = entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    findings.Add(new PlainCopyFinding(relative, 0, DateTime.UtcNow, PlainCopyKinds.Unreadable, "attributes cannot be read"));
                    continue;
                }
                if (isLink)
                {
                    findings.Add(new PlainCopyFinding(relative, 0, SafeLastWrite(entry), PlainCopyKinds.Link, "symbolic link or reparse point: not followed"));
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                var topLevel = string.Equals(directory, root, StringComparison.Ordinal);
                if (topLevel && liveNames.Contains(entry.Name))
                {
                    if (mode == PlainScanMode.Preflight) liveFiles.Add((relative, entry.FullName));
                    continue;
                }

                var finding = ScanFile((FileInfo)entry, relative, limits);
                if (finding is null) continue;
                findings.Add(finding);
                if (mode == PlainScanMode.Preflight && finding.Kind is PlainCopyKinds.PlainSqlite or PlainCopyKinds.PlainJournal)
                    identityCandidates.Add((finding, entry.FullName));
            }
        }

        if (mode == PlainScanMode.Preflight) CheckIdentities(findings, liveFiles, identityCandidates);

        findings.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return findings;
    }

    private static HashSet<string> LiveNames(PlainScanMode mode)
    {
        string[] bases = mode == PlainScanMode.Preflight
            ? [VaultStoreProbe.Database, VaultStoreProbe.Chat]
            : [VaultStoreProbe.Boot];
        var names = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var name in bases)
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                names.Add(name + suffix);
        return names;
    }

    private static string Relative(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.UtcNow; }
    }

    // ---- identities: another name of a live file ---------------------------------------------------------------------------------

    private static void CheckIdentities(List<PlainCopyFinding> findings, List<(string Relative, string Full)> liveFiles,
        List<(PlainCopyFinding Finding, string Full)> candidates)
    {
        var live = new List<(string Relative, FileIdentityInfo Id)>();
        foreach (var (relative, full) in liveFiles)
        {
            var id = FileIdentity.TryGet(full);
            if (id is null)
            {
                findings.Add(new PlainCopyFinding(relative, SafeLength(full), SafeLastWrite(new FileInfo(full)), PlainCopyKinds.IdentityUnavailable,
                    "the identity of this file cannot be read on this platform"));
                continue;
            }
            live.Add((relative, id.Value));
            if (id.Value.LinkCount > 1)
                findings.Add(new PlainCopyFinding(relative, SafeLength(full), SafeLastWrite(new FileInfo(full)), PlainCopyKinds.LiveLinked,
                    $"this file has {id.Value.LinkCount} names; another name exists somewhere"));
        }

        foreach (var (finding, full) in candidates)
        {
            var id = FileIdentity.TryGet(full);
            if (id is null) continue; // stays a plain finding: it blocks anyway
            var match = live.FirstOrDefault(l => l.Id.IsSameFileAs(id.Value));
            if (match.Relative is null) continue;
            findings[findings.IndexOf(finding)] = finding with { Kind = PlainCopyKinds.SharesIdentity, Detail = $"another name for {match.Relative}" };
        }
    }

    private static long SafeLength(string full)
    {
        try { return new FileInfo(full).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ---- one file --------------------------------------------------------------------------------------------------------------

    private static PlainCopyFinding? ScanFile(FileInfo info, string relative, PlainScanLimits limits)
    {
        long length;
        DateTime written;
        try
        {
            length = info.Length;
            written = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PlainCopyFinding(relative, 0, DateTime.UtcNow, PlainCopyKinds.Unreadable, "size cannot be read");
        }

        PlainCopyFinding Make(string kind, string? detail = null) => new(relative, length, written, kind, detail);

        try
        {
            using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var head = new byte[HeadSize];
            var n = fs.ReadAtLeast(head, HeadSize, throwOnEndOfStream: false);
            var span = head.AsSpan(0, n);

            if (n >= 16 && span[..16].SequenceEqual(SqliteMagic))
            {
                // A boot store carries only allow-listed columns (spec 2.3), so a copy of it anywhere is as exposed as the original.
                if (n >= 72 && BinaryPrimitives.ReadInt32BigEndian(span[68..]) == VaultStoreProbe.BootApplicationId) return null;
                return Make(PlainCopyKinds.PlainSqlite);
            }

            if (VaultStoreProbe.IsWalHeader(span) || VaultStoreProbe.IsJournalHeader(span))
            {
                // The magic alone decides nothing: a sealed file's own WAL and journal have the same plain header (Appendix C).
                return VaultStoreProbe.InspectStream(fs) == ProbeFileState.Plain ? Make(PlainCopyKinds.PlainJournal) : null;
            }

            if (IsGzipHeader(span)) return ScanGzip(fs, relative, length, written, limits);
            if (IsTarHeader(span)) return ToFinding(ScanTar(fs, limits, fs.Length), relative, length, written);
            if (IsZipHeader(span)) return ToFinding(ZipScanner.Scan(fs, limits), relative, length, written);
            if (OtherCompressionFormat(span) is { } format) return Make(PlainCopyKinds.UnverifiedArchive, $"an archive of a kind this version cannot look into ({format})");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Make(PlainCopyKinds.Unreadable, "the file cannot be opened or read");
        }
    }

    private static PlainCopyFinding? ToFinding(ArchiveVerdict verdict, string relative, long length, DateTime written) => verdict.Kind switch
    {
        ArchiveVerdictKind.Fine => null,
        ArchiveVerdictKind.PlainDatabase => new PlainCopyFinding(relative, length, written, PlainCopyKinds.PlainArchive, verdict.Detail),
        _ => new PlainCopyFinding(relative, length, written, PlainCopyKinds.UnverifiedArchive, verdict.Detail),
    };

    // ---- formats ---------------------------------------------------------------------------------------------------------------

    internal static bool IsGzipHeader(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x1F && b[1] == 0x8B && b[2] == 0x08 && (b[3] & 0xE0) == 0;

    internal static bool IsZipHeader(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && ((b[2] == 3 && b[3] == 4) || (b[2] == 5 && b[3] == 6));

    /// <summary>A 512-byte block that is a tar header: a valid header checksum (the magic <c>ustar</c> is not required, so v7, pax and GNU tars count).</summary>
    internal static bool IsTarHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < 512 || b[0] == 0) return false;
        // The checksum field (bytes 148-155): octal digits, padded with spaces and NULs.
        long stored = 0;
        var digits = 0;
        for (var i = 148; i < 156; i++)
        {
            var c = b[i];
            if (c == 0 || c == ' ')
            {
                if (digits > 0) break;
                continue;
            }
            if (c < '0' || c > '7') return false;
            stored = stored * 8 + (c - '0');
            digits++;
        }
        if (digits == 0) return false;
        long unsignedSum = 0, signedSum = 0;
        for (var i = 0; i < 512; i++)
        {
            var c = i is >= 148 and < 156 ? (byte)' ' : b[i];
            unsignedSum += c;
            signedSum += (sbyte)c;
        }
        return stored == unsignedSum || stored == signedSum;
    }

    /// <summary>The name of a compression or archive format this version cannot open, or null.</summary>
    internal static string? OtherCompressionFormat(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 10 && b[0] == 'B' && b[1] == 'Z' && b[2] == 'h' && b[3] >= '1' && b[3] <= '9'
            && b[4] == 0x31 && b[5] == 0x41 && b[6] == 0x59 && b[7] == 0x26 && b[8] == 0x53 && b[9] == 0x59) return "bzip2";
        if (b.Length >= 6 && b[..6].SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 })) return "xz";
        if (b.Length >= 4 && b[..4].SequenceEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD })) return "zstd";
        if (b.Length >= 4 && b[..4].SequenceEqual(new byte[] { 0x04, 0x22, 0x4D, 0x18 })) return "lz4";
        if (b.Length >= 6 && b[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return "7-Zip";
        if (b.Length >= 6 && b[..6].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 })) return "RAR";
        return null;
    }

    // ---- archives --------------------------------------------------------------------------------------------------------------

    internal enum ArchiveVerdictKind { Fine, PlainDatabase, Unverified }

    internal readonly record struct ArchiveVerdict(ArchiveVerdictKind Kind, string? Detail = null)
    {
        public static readonly ArchiveVerdict Fine = new(ArchiveVerdictKind.Fine);
        public static ArchiveVerdict Plain(string member) => new(ArchiveVerdictKind.PlainDatabase, $"a plain SQLite database is the member \"{member}\"");
        public static ArchiveVerdict Unverified(string why) => new(ArchiveVerdictKind.Unverified, why);
    }

    /// <summary>The verdict on one archive member from its first bytes (the member's name, size and position are never looked at).</summary>
    internal static ArchiveVerdict JudgeMember(ReadOnlySpan<byte> first, string name)
    {
        if (first.Length >= 16 && first[..16].SequenceEqual(SqliteMagic)) return ArchiveVerdict.Plain(name);
        if (IsGzipHeader(first) || IsZipHeader(first) || IsTarHeader(first) || OtherCompressionFormat(first) is not null)
            return ArchiveVerdict.Unverified("an archive inside an archive, which this version does not open");
        return ArchiveVerdict.Fine;
    }

    private static PlainCopyFinding? ScanGzip(FileStream fs, string relative, long length, DateTime written, PlainScanLimits limits)
    {
        try
        {
            fs.Position = 0;
            var head = new byte[HeadSize];
            int n;
            using (var probe = new LimitedReadStream(new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true), limits.MaxInflatedBytes))
            {
                n = probe.ReadAtLeast(head, HeadSize, throwOnEndOfStream: false);
                var span = head.AsSpan(0, n);
                if (n >= 16 && span[..16].SequenceEqual(SqliteMagic))
                    return new PlainCopyFinding(relative, length, written, PlainCopyKinds.PlainArchive, "the gzip content is a plain SQLite database");
                if (!IsTarHeader(span))
                {
                    // The prefix only decides whether this is a tar. A non-tar gzip still has to be
                    // consumed to its footer, otherwise a damaged CRC/trailer after the prefix is accepted.
                    probe.CopyTo(Stream.Null);
                    return null; // a rotated log, or anything else that is not a database or a tar
                }
            }

            fs.Position = 0;
            using var inflated = new LimitedReadStream(new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true), limits.MaxInflatedBytes);
            return ToFinding(ScanTar(inflated, limits, knownLength: -1), relative, length, written);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArchiveLimitException or NotSupportedException)
        {
            return new PlainCopyFinding(relative, length, written, PlainCopyKinds.UnverifiedArchive, DescribeFailure(ex));
        }
    }

    /// <summary>Reads a tar from its first entry to its end; every regular-file member is judged by its first bytes.</summary>
    private static ArchiveVerdict ScanTar(Stream stream, PlainScanLimits limits, long knownLength)
    {
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            using var reader = new TarReader(stream, leaveOpen: true);
            var count = 0;
            var buffer = new byte[HeadSize];
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                if (++count > limits.MaxArchiveEntries) return ArchiveVerdict.Unverified($"more than {limits.MaxArchiveEntries} entries");
                if (entry.EntryType is TarEntryType.Directory or TarEntryType.SymbolicLink or TarEntryType.HardLink
                    or TarEntryType.CharacterDevice or TarEntryType.BlockDevice or TarEntryType.Fifo
                    or TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                    continue;
                if (entry.DataStream is null) continue;
                if (knownLength >= 0 && entry.DataOffset >= 0 && entry.DataOffset + entry.Length > knownLength)
                    return ArchiveVerdict.Unverified("the archive is cut inside an entry");
                var read = entry.DataStream.ReadAtLeast(buffer, (int)Math.Min(HeadSize, entry.Length), throwOnEndOfStream: false);
                if (read < Math.Min(HeadSize, entry.Length)) return ArchiveVerdict.Unverified("the archive is cut inside an entry");
                var verdict = JudgeMember(buffer.AsSpan(0, read), entry.Name);
                if (verdict.Kind != ArchiveVerdictKind.Fine) return verdict;
                entry.DataStream.CopyTo(Stream.Null);
            }
            // TarReader stops at the end-of-archive blocks; consume the wrapped stream as well so a
            // gzip footer or a truncated stream cannot hide after a valid-looking tar prefix.
            stream.CopyTo(Stream.Null);
            return ArchiveVerdict.Fine;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArchiveLimitException or NotSupportedException
                                       or FormatException or ArgumentException or OverflowException)
        {
            // Any other IOException is a read error on the file itself and propagates: the file is then "unreadable", not "unverified".
            return ArchiveVerdict.Unverified(DescribeFailure(ex));
        }
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        ArchiveLimitException => "over the size limit of the scan",
        EndOfStreamException => "the archive is cut short",
        InvalidDataException => "the archive is damaged or in a layout this version cannot read",
        NotSupportedException => "an entry type or layout this version cannot read",
        _ => "the archive could not be read to its end",
    };

    /// <summary>A read-through stream that stops the scan once an archive has inflated more than the limit.</summary>
    internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

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
            var n = inner.Read(destination);
            _read += n;
            if (_read > limit) throw new ArchiveLimitException();
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class ArchiveLimitException : Exception;

    /// <summary>The central directory of a zip, read once more by hand for the two facts <see cref="ZipArchive"/> does not show: encryption and the compression method.</summary>
    private static class ZipScanner
    {
        private const int EocdSignature = 0x06054b50, CentralSignature = 0x02014b50;

        public static ArchiveVerdict Scan(FileStream fs, PlainScanLimits limits)
        {
            try
            {
                var entries = ReadCentralDirectory(fs, limits, out var failure);
                if (failure is not null) return ArchiveVerdict.Unverified(failure);

                fs.Position = 0;
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count != entries.Count) return ArchiveVerdict.Unverified("the central directory is inconsistent");
                var buffer = new byte[HeadSize];
                long inflated = 0;
                for (var i = 0; i < archive.Entries.Count; i++)
                {
                    var entry = archive.Entries[i];
                    var (flags, method) = entries[i];
                    if ((flags & 0x1) != 0 || (flags & 0x40) != 0) return ArchiveVerdict.Unverified("an encrypted entry");
                    if (method is not (0 or 8 or 9)) return ArchiveVerdict.Unverified($"an entry compressed with an unsupported method ({method})");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a folder entry carries no bytes
                    using var data = entry.Open();
                    var read = data.ReadAtLeast(buffer, HeadSize, throwOnEndOfStream: false);
                    inflated += read;
                    if (inflated > limits.MaxInflatedBytes) return ArchiveVerdict.Unverified("over the size limit of the scan");
                    var verdict = JudgeMember(buffer.AsSpan(0, read), entry.FullName);
                    if (verdict.Kind != ArchiveVerdictKind.Fine) return verdict;
                    inflated = DrainToEnd(data, inflated, limits.MaxInflatedBytes);
                }
                return ArchiveVerdict.Fine;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArchiveLimitException or NotSupportedException
                                           or FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                return ArchiveVerdict.Unverified(DescribeFailure(ex));
            }
        }

        private static long DrainToEnd(Stream stream, long total, long limit)
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return total;
                total += read;
                if (total > limit) throw new ArchiveLimitException();
            }
        }

        private static List<(ushort Flags, ushort Method)> ReadCentralDirectory(FileStream fs, PlainScanLimits limits, out string? failure)
        {
            failure = null;
            var result = new List<(ushort, ushort)>();
            var length = fs.Length;
            var tailSize = (int)Math.Min(length, 65557);
            var tail = new byte[tailSize];
            fs.Position = length - tailSize;
            fs.ReadExactly(tail);
            var at = -1;
            for (var i = tailSize - 22; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(i)) == EocdSignature) { at = i; break; }
            }
            if (at < 0) { failure = "the central directory of the zip cannot be found"; return result; }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 10));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 12));
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at + 16));
            if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
            {
                failure = "a zip64 archive, which this version does not open";
                return result;
            }
            if (count > limits.MaxArchiveEntries) { failure = $"more than {limits.MaxArchiveEntries} entries"; return result; }
            if (offset + size > length) { failure = "the central directory of the zip is cut short"; return result; }

            var directory = new byte[size];
            fs.Position = offset;
            fs.ReadExactly(directory);
            var p = 0;
            for (var i = 0; i < count; i++)
            {
                if (p + 46 > directory.Length || BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(p)) != CentralSignature)
                {
                    failure = "the central directory of the zip is damaged";
                    return result;
                }
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 8));
                var method = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 10));
                int name = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 28));
                int extra = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 30));
                int comment = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(p + 32));
                result.Add((flags, method));
                p += 46 + name + extra + comment;
            }
            return result;
        }
    }
}

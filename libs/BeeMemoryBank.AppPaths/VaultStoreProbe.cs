using System.Buffers.Binary;

namespace BeeMemoryBank.AppPaths;

/// <summary>What a companion file of a sealed pair is (docs/design/step-b-spec.md 2.5, 7.6). The AppPaths twin of <c>CompanionState</c> in Storage.</summary>
public enum ProbeFileState
{
    /// <summary>The file does not exist.</summary>
    Absent,
    /// <summary>Empty, or a file that holds no plain page image (a sealed file, a WAL or journal of encrypted pages).</summary>
    Fine,
    /// <summary>A plain SQLite file, or a WAL or rollback journal with at least one plain page image.</summary>
    Plain,
    /// <summary>The file exists and cannot be read: fail closed.</summary>
    Unreadable,
}

/// <summary>
/// The start-time checks of <c>VaultStoreFiles.Diagnose</c> (BeeMemoryBank.Storage, contract A.2) written once more where AppPaths can use
/// them: AppPaths is a library with no project reference, so the resolver of 10.6, <see cref="VaultFiles.Inspect"/> and the scanner
/// cannot call Storage. A test in a project that references both assemblies feeds the same real files to both and asserts the answers
/// agree (docs/design/step-b-spec.md 7.6, 15.1.15), so the two copies cannot drift. No SQLite connection is opened; nothing is changed.
/// </summary>
public static class VaultStoreProbe
{
    public const string Database = "beememorybank.db";
    public const string Chat = "chat.db";
    public const string Boot = "beememorybank.boot.db";
    public const string SealJournal = "seal-journal.json";
    public const string SealNewSuffix = ".seal-new";
    public const string SealOldSuffix = ".seal-old";

    /// <summary><c>PRAGMA application_id</c> of every boot store: "BMBS".</summary>
    public const int BootApplicationId = 0x424D4253;

    /// <summary>The files beside a sealed pair that can still hold pages in clear (spec 2.5); a <c>-shm</c> holds only the WAL index.</summary>
    public static readonly IReadOnlyList<string> Companions =
        [Chat, Database + "-wal", Database + "-journal", Chat + "-wal", Chat + "-journal"];

    private const int MaxFramesPerEnd = 1024;
    private const int PageSize = 4096;

    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    private static ReadOnlySpan<byte> JournalMagic => [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    /// <summary>First 16 bytes are <c>SQLite format 3\0</c>. False for an unreadable or short file.</summary>
    public static bool HasSqliteMagic(string path)
    {
        if (LinkEntry.IsLink(path)) return false;   // a link is not opened: what is behind it is not this folder's file
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            return fs.Read(header) == header.Length && header.SequenceEqual(SqliteMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Header bytes 68-71 (big-endian) of a plain SQLite file, or 0 when unreadable.</summary>
    public static int ReadApplicationId(string path)
    {
        if (LinkEntry.IsLink(path)) return 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[72];
            return fs.Read(header) == header.Length ? BinaryPrimitives.ReadInt32BigEndian(header[68..]) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>True when the file is a boot store: the SQLite magic and our application id.</summary>
    public static bool IsBootStore(string path) => HasSqliteMagic(path) && ReadApplicationId(path) == BootApplicationId;

    /// <summary>
    /// True when <paramref name="dataDir"/> holds a boot store of ours beside a non-empty database without the SQLite magic whose length is a
    /// multiple of 4096, and no plain companion (spec 2.5). The Sealed branch of <c>VaultStoreFiles.Diagnose</c>, without its
    /// <c>*.seal-old*</c> rule: the resolver of 10.6 asks about the live pair while leftovers are still lying beside it.
    /// </summary>
    public static bool IsSealedPair(string dataDir)
    {
        // A data folder that is a link, or a reserved name in it that is one, is not probed through: it is not a sealed pair (the entry points refuse it by name first).
        if (LinkGate.Linked(dataDir).Count > 0) return false;
        var db = Path.Combine(dataDir, Database);
        var boot = Path.Combine(dataDir, Boot);
        try
        {
            if (!File.Exists(boot) || !IsBootStore(boot)) return false;
            if (!File.Exists(db)) return false;
            var length = new FileInfo(db).Length;
            if (length == 0 || HasSqliteMagic(db) || length % PageSize != 0) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return Companions.All(name => InspectCompanion(Path.Combine(dataDir, name)) is ProbeFileState.Absent or ProbeFileState.Fine);
    }

    /// <summary>The companion files (<see cref="Companions"/>) that hold pages in clear, or cannot be read (fail closed), relative to <paramref name="dataDir"/>.</summary>
    public static IReadOnlyList<string> PlainCompanions(string dataDir) =>
        Companions.Where(name => InspectCompanion(Path.Combine(dataDir, name)) is ProbeFileState.Plain or ProbeFileState.Unreadable).ToList();

    /// <summary>True when a file named <c>beememorybank.db.seal-old*</c> or <c>chat.db.seal-old*</c> (a replaced plain file or its sidecar) is in the directory.</summary>
    public static bool HasSealOldLeftovers(string dataDir)
    {
        foreach (var _ in SealOldLeftovers(dataDir)) return true;
        return false;
    }

    /// <summary>The names of those files, relative to the data directory.</summary>
    public static IEnumerable<string> SealOldLeftovers(string dataDir)
    {
        if (LinkEntry.IsLink(RekeySwapJournal.Normalize(dataDir)) || !Directory.Exists(dataDir)) yield break;   // a linked folder is not enumerated
        foreach (var pattern in new[] { Database + SealOldSuffix + "*", Chat + SealOldSuffix + "*" })
            foreach (var file in Directory.EnumerateFiles(dataDir, pattern))
                yield return Path.GetFileName(file);
    }

    /// <summary>
    /// Whether a companion file of a sealed pair holds pages in clear (spec 2.5): a plain <c>chat.db</c>, or a WAL or rollback journal with
    /// at least one plain page image (see <see cref="LooksLikePlainPage"/>). A sealed file's own WAL and journal have a plain header and
    /// encrypted pages, so the magic of a WAL or journal proves nothing; only the page images count.
    /// </summary>
    public static ProbeFileState InspectCompanion(string path)
    {
        try
        {
            if (LinkEntry.IsLink(path)) return ProbeFileState.Unreadable;   // fail closed: a link is never opened, never read as "fine" or "plain"
            if (!File.Exists(path)) return ProbeFileState.Absent;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return InspectStream(fs);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ProbeFileState.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProbeFileState.Unreadable;
        }
    }

    /// <summary>
    /// <see cref="InspectCompanion"/> on a stream that is already open (the scanner reads each file once). Reads from the start of the stream;
    /// <see cref="ProbeFileState.Fine"/> for an empty or unrecognised file (SQLite ignores it).
    /// </summary>
    public static ProbeFileState InspectStream(FileStream fs)
    {
        if (fs.Length == 0) return ProbeFileState.Fine;
        fs.Position = 0;
        Span<byte> head = stackalloc byte[32];
        var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (n >= 16 && head[..16].SequenceEqual(SqliteMagic)) return ProbeFileState.Plain;

        if (n >= 32 && IsWalHeader(head, out var walPageSize))
            return HoldsPlainPage(fs, first: 32, prefix: 24, pageSize: walPageSize, suffix: 0) ? ProbeFileState.Plain : ProbeFileState.Fine;

        if (n >= 28 && head[..8].SequenceEqual(JournalMagic))
        {
            var sector = BinaryPrimitives.ReadUInt32BigEndian(head[20..]);
            var page = BinaryPrimitives.ReadUInt32BigEndian(head[24..]);
            if (IsPageSize(sector) && IsPageSize(page))
                return HoldsPlainPage(fs, first: sector, prefix: 4, pageSize: (int)page, suffix: 4) ? ProbeFileState.Plain : ProbeFileState.Fine;
        }
        return ProbeFileState.Fine;
    }

    /// <summary>True when <paramref name="head"/> (at least 32 bytes) starts a WAL: magic 0x377F0682 or 0x377F0683, version 3007000, a power-of-two page size of 512 to 65536.</summary>
    public static bool IsWalHeader(ReadOnlySpan<byte> head) => head.Length >= 32 && IsWalHeader(head, out _);

    /// <summary>True when <paramref name="head"/> (at least 28 bytes) starts a rollback journal with a sane sector and page size.</summary>
    public static bool IsJournalHeader(ReadOnlySpan<byte> head) =>
        head.Length >= 28 && head[..8].SequenceEqual(JournalMagic)
        && IsPageSize(BinaryPrimitives.ReadUInt32BigEndian(head[20..])) && IsPageSize(BinaryPrimitives.ReadUInt32BigEndian(head[24..]));

    private static bool IsPageSize(uint v) => v is >= 512 and <= 65536 && (v & (v - 1)) == 0;

    private static bool IsWalHeader(ReadOnlySpan<byte> h, out int pageSize)
    {
        var magic = BinaryPrimitives.ReadUInt32BigEndian(h);
        var page = BinaryPrimitives.ReadUInt32BigEndian(h[8..]);
        pageSize = (int)page;
        return (magic == 0x377F0682 || magic == 0x377F0683) && BinaryPrimitives.ReadUInt32BigEndian(h[4..]) == 3007000 && IsPageSize(page);
    }

    /// <summary>
    /// Reads the complete frames (records) of a WAL (journal) - at most the first and the last <see cref="MaxFramesPerEnd"/> - and reports
    /// whether any holds a plain page image. A record is <c>prefix + pageSize + suffix</c> bytes from <paramref name="first"/>; its first four
    /// bytes are the page number.
    /// </summary>
    private static bool HoldsPlainPage(FileStream fs, long first, int prefix, int pageSize, int suffix)
    {
        long record = (long)prefix + pageSize + suffix;
        var count = (fs.Length - first) / record;
        if (count <= 0) return false;
        var buffer = new byte[prefix + pageSize];
        for (var i = 0L; i < count; i++)
        {
            if (i == MaxFramesPerEnd && count - i > MaxFramesPerEnd) i = count - MaxFramesPerEnd;
            fs.Seek(first + i * record, SeekOrigin.Begin);
            if (fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length) break;
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (LooksLikePlainPage(buffer.AsSpan(prefix, pageSize), pageNumber == 1)) return true;
        }
        return false;
    }

    /// <summary>
    /// The plain page test (spec 2.5): a page image is plain when it starts with the SQLite magic (page 1) or has a valid b-tree page header -
    /// the type byte 2, 5, 10 or 13 at offset 0 (offset 100 on page 1), at most 60 fragmented bytes, the cell pointer array ending at or before
    /// the cell content start, the content start inside the page, the first free block 0 or inside the page, and every cell pointer between the
    /// end of the pointer array and the page size minus 4. Every table or index page passes; a ciphertext page passes with a probability of about 2^-32.
    /// </summary>
    public static bool LooksLikePlainPage(ReadOnlySpan<byte> page, bool firstPageOfDatabase)
    {
        if (firstPageOfDatabase && page.Length >= 16 && page[..16].SequenceEqual(SqliteMagic)) return true;
        var o = firstPageOfDatabase ? 100 : 0;
        if (page.Length < o + 14) return false;

        int header;
        switch (page[o])
        {
            case 0x0A or 0x0D: header = 8; break;
            case 0x02 or 0x05: header = 12; break;
            default: return false;
        }
        if (page[o + 7] > 60) return false;

        int freeBlock = BinaryPrimitives.ReadUInt16BigEndian(page[(o + 1)..]);
        int cells = BinaryPrimitives.ReadUInt16BigEndian(page[(o + 3)..]);
        int content = BinaryPrimitives.ReadUInt16BigEndian(page[(o + 5)..]);
        if (content == 0) content = 65536;

        var pointersEnd = o + header + 2 * cells;
        var last = page.Length - 4;
        if (pointersEnd > content || content > page.Length) return false;
        if (freeBlock != 0 && (freeBlock < pointersEnd || freeBlock > last)) return false;
        for (var i = 0; i < cells; i++)
        {
            int pointer = BinaryPrimitives.ReadUInt16BigEndian(page[(o + header + 2 * i)..]);
            if (pointer < pointersEnd || pointer > last) return false;
        }
        return true;
    }
}

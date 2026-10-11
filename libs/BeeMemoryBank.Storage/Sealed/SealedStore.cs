using System.Buffers.Binary;
using System.Text;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>What a data directory holds (docs/design/step-b-spec.md 2.1).</summary>
public enum VaultStoreMode
{
    /// <summary>No boot store: a plain database (or none yet). Today's behaviour, unchanged.</summary>
    Plain,
    /// <summary>A boot store and an encrypted database, and no plain companion: closed until a door opens it.</summary>
    Sealed,
    /// <summary>One half without the other, a boot store that is not ours, or a plain companion beside a sealed pair: the node refuses to start (spec 2.5).</summary>
    Broken,
}

/// <summary>
/// The answer of <see cref="VaultStoreFiles.Diagnose"/> (spec 2.5): the mode and, for Broken, a stable reason code
/// (<c>db_without_boot</c>, <c>boot_beside_plain_db</c>, <c>boot_not_ours</c>, <c>db_missing_or_empty</c>, <c>db_not_page_aligned</c>,
/// <c>seal_leftovers_no_journal</c>, <c>plain_companion</c>, <c>companion_unreadable</c>) and the names, relative to the data
/// directory, that the reason concerns. No content of any file.
/// </summary>
public sealed record VaultStoreDiagnosis(VaultStoreMode Mode, string? Reason, IReadOnlyList<string> Files)
{
    public static VaultStoreDiagnosis Of(VaultStoreMode mode) => new(mode, null, Array.Empty<string>());

    public static VaultStoreDiagnosis Broken(string reason, params string[] files) => new(VaultStoreMode.Broken, reason, files);
}

/// <summary>What a companion file of a sealed pair is (spec 2.5).</summary>
public enum CompanionState
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

/// <summary>File names and the start-time classification of a data directory (spec 2.1, 2.5). No key, no SQLite connection.</summary>
public static class VaultStoreFiles
{
    public const string Database = "beememorybank.db";
    public const string Chat = "chat.db";
    public const string Boot = "beememorybank.boot.db";
    public const string SealJournal = "seal-journal.json";
    /// <summary>Suffix of a migration target next to its live file (spec 10.6).</summary>
    public const string SealNewSuffix = ".seal-new";
    /// <summary>Suffix a replaced plain file carries between the swap and its wipe (spec 10.6); never survives a start.</summary>
    public const string SealOldSuffix = ".seal-old";
    /// <summary>Folder of the pre-encryption snapshot (spec 10.4 step 3, 10.7): not read by the snapshot listing or its retention.</summary>
    public const string PreEncryptionDir = "pre-encryption";

    /// <summary><c>PRAGMA application_id</c> of every boot store: "BMBS".</summary>
    public const int BootApplicationId = 0x424D4253;

    /// <summary>The files beside a sealed pair that can still hold pages in clear (spec 2.5); a <c>-shm</c> holds only the WAL index.</summary>
    public static readonly IReadOnlyList<string> Companions =
        [Chat, Database + "-wal", Database + "-journal", Chat + "-wal", Chat + "-journal"];

    private const int MaxFramesPerEnd = 1024;

    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    private static ReadOnlySpan<byte> JournalMagic => [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    /// <summary>The mode of <see cref="Diagnose"/>. Called after the seal journal is resolved (spec 10.6) and before anything opens the database.</summary>
    public static VaultStoreMode DetectMode(string dataPath) => Diagnose(dataPath).Mode;

    /// <summary>
    /// Classifies a data directory and, for Broken, says why and which files (spec 2.5). Reads at most the five companion files and the
    /// three store files; deletes, moves and changes nothing. Called after the seal journal is resolved (spec 10.6), at every start.
    /// </summary>
    public static VaultStoreDiagnosis Diagnose(string dataPath)
    {
        // A replaced plain file that is still there, with no journal to say that its wipe is pending: refuse (spec 10.6). The resolver
        // always decides this case first; this is the backstop for a start that skipped it.
        if (HasSealOldLeftovers(dataPath) && !File.Exists(Path.Combine(dataPath, SealJournal)))
            return VaultStoreDiagnosis.Broken("seal_leftovers_no_journal", new List<string>(SealOldLeftovers(dataPath)).ToArray());

        var db = Path.Combine(dataPath, Database);
        var boot = Path.Combine(dataPath, Boot);
        var dbExists = File.Exists(db) && new FileInfo(db).Length > 0;

        if (File.Exists(boot))
        {
            if (!HasSqliteMagic(boot) || ReadApplicationId(boot) != BootApplicationId) return VaultStoreDiagnosis.Broken("boot_not_ours", Boot);
            if (!dbExists) return VaultStoreDiagnosis.Broken("db_missing_or_empty", Database);
            if (HasSqliteMagic(db)) return VaultStoreDiagnosis.Broken("boot_beside_plain_db", Boot, Database);
            // A sealed file is whole 4096-byte pages: a length that is not a multiple means a copy cut inside a page (spec 8.7.2).
            if (new FileInfo(db).Length % SqlCipherScheme.PageSize != 0) return VaultStoreDiagnosis.Broken("db_not_page_aligned", Database);

            // A sealed pair is Sealed only when nothing beside it holds pages in clear (spec 2.5, every start).
            List<string> plain = [], unreadable = [];
            foreach (var name in Companions)
            {
                switch (InspectCompanion(Path.Combine(dataPath, name)))
                {
                    case CompanionState.Plain: plain.Add(name); break;
                    case CompanionState.Unreadable: unreadable.Add(name); break;
                }
            }
            if (plain.Count > 0) return VaultStoreDiagnosis.Broken("plain_companion", plain.ToArray());
            if (unreadable.Count > 0) return VaultStoreDiagnosis.Broken("companion_unreadable", unreadable.ToArray());
            return VaultStoreDiagnosis.Of(VaultStoreMode.Sealed);
        }

        // An encrypted database without its boot store cannot be opened by anyone: refuse rather than start empty beside it.
        return dbExists && !HasSqliteMagic(db)
            ? VaultStoreDiagnosis.Broken("db_without_boot", Database)
            : VaultStoreDiagnosis.Of(VaultStoreMode.Plain);
    }

    /// <summary>True when a file named <c>beememorybank.db.seal-old*</c> or <c>chat.db.seal-old*</c> (a replaced plain file or its sidecar) is in the directory.</summary>
    public static bool HasSealOldLeftovers(string dataPath)
    {
        foreach (var _ in SealOldLeftovers(dataPath)) return true;
        return false;
    }

    /// <summary>The names of those files, relative to the data directory.</summary>
    public static IEnumerable<string> SealOldLeftovers(string dataPath)
    {
        if (!Directory.Exists(dataPath)) yield break;
        foreach (var pattern in new[] { Database + SealOldSuffix + "*", Chat + SealOldSuffix + "*" })
            foreach (var file in Directory.EnumerateFiles(dataPath, pattern))
                yield return Path.GetFileName(file);
    }

    /// <summary>
    /// Whether a companion file of a sealed pair holds pages in clear (spec 2.5). A plain <c>chat.db</c> starts with the SQLite magic. A
    /// WAL or rollback journal of a SEALED file has a plain header and encrypted page images (spec Appendix C), so only its page images
    /// count: the file is Plain when any sampled frame (record) is a plain page image - the SQLite magic on page 1 or a valid b-tree page
    /// header (<see cref="LooksLikePlainPage"/>). Empty and unrecognised files are Fine (SQLite ignores them); an unreadable file is not.
    /// </summary>
    public static CompanionState InspectCompanion(string path)
    {
        try
        {
            if (!File.Exists(path)) return CompanionState.Absent;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return CompanionState.Fine;
            Span<byte> head = stackalloc byte[32];
            var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (n >= 16 && head[..16].SequenceEqual(SqliteMagic)) return CompanionState.Plain;

            if (n >= 32 && IsWalHeader(head, out var walPageSize))
                return HoldsPlainPage(fs, first: 32, prefix: 24, pageSize: walPageSize, suffix: 0) ? CompanionState.Plain : CompanionState.Fine;

            if (n >= 28 && head[..8].SequenceEqual(JournalMagic))
            {
                var sector = BinaryPrimitives.ReadUInt32BigEndian(head[20..]);
                var page = BinaryPrimitives.ReadUInt32BigEndian(head[24..]);
                if (IsPageSize(sector) && IsPageSize(page))
                    return HoldsPlainPage(fs, first: sector, prefix: 4, pageSize: (int)page, suffix: 4) ? CompanionState.Plain : CompanionState.Fine;
            }
            return CompanionState.Fine;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return CompanionState.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CompanionState.Unreadable;
        }
    }

    private static bool IsPageSize(uint v) => v is >= 512 and <= 65536 && (v & (v - 1)) == 0;

    /// <summary>A WAL header: magic 0x377F0682 or 0x377F0683, version 3007000, a power-of-two page size from 512 to 65536.</summary>
    private static bool IsWalHeader(ReadOnlySpan<byte> h, out int pageSize)
    {
        var magic = BinaryPrimitives.ReadUInt32BigEndian(h);
        var page = BinaryPrimitives.ReadUInt32BigEndian(h[8..]);
        pageSize = (int)page;
        return (magic == 0x377F0682 || magic == 0x377F0683) && BinaryPrimitives.ReadUInt32BigEndian(h[4..]) == 3007000 && IsPageSize(page);
    }

    /// <summary>
    /// Reads the complete frames (records) of a WAL (journal) - at most the first <see cref="MaxFramesPerEnd"/> and the last
    /// <see cref="MaxFramesPerEnd"/> - and reports whether any holds a plain page image. A record is <c>prefix + pageSize + suffix</c>
    /// bytes from <paramref name="first"/>; its first four bytes are the page number.
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

    public static bool HasSqliteMagic(string path)
    {
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
}

/// <summary>The two encrypted files of a sealed node, each with its own page key (spec 3.2).</summary>
public static class DbPurposes
{
    public const string Vault = "vault";
    public const string Chat = "chat";
}

/// <summary>
/// The page keys of an open sealed vault (spec 3.2, 7.1). Implemented by the vault gate (track 3), read by the connection factories
/// (track 1). Absent (null) on a blind host and in tests that do not seal: the factories then behave exactly as before Step B.
/// </summary>
public interface IVaultKeySource
{
    VaultStoreMode Mode { get; }

    /// <summary>True while the page keys are in memory.</summary>
    bool IsOpen { get; }

    /// <summary>Changes on every open; a factory keeps no pooled handle across a change.</summary>
    long OpenGeneration { get; }

    /// <summary>A copy of the 32-byte page key of <paramref name="purpose"/>; the caller clears it. Throws <c>VaultSealedException</c> when closed.</summary>
    byte[] GetPageKey(string purpose);
}

/// <summary>
/// The cipher recipe (spec 7.1), checked against SQLite3MC 2.4.0 (spec Appendix C): SQLCipher 4 page format chosen per connection
/// through the URI (sqlite3mc_config('default:...') only changes the connection it runs on), the key given as SQLCipher raw-key
/// syntax, never as a passphrase. Every string these helpers return that contains a key must never be logged or put in an exception.
/// </summary>
public static class SqlCipherScheme
{
    public const int PageSize = 4096;
    public const int ReservedBytes = 80;

    /// <summary>The Data Source of a keyed connection (no key in it: safe as a cache key or in a log).</summary>
    public static string DataSource(string path) => "file://" + ToUriPath(path) + "?cipher=sqlcipher&legacy=4";

    /// <summary>The Password of a keyed connection: <c>x'&lt;64 hex&gt;'</c>, which Microsoft.Data.Sqlite sends as <c>PRAGMA key</c>.</summary>
    public static string Password(ReadOnlySpan<byte> pageKey) => "x'" + Convert.ToHexStringLower(pageKey) + "'";

    /// <summary>
    /// The quoted SQL target of <c>VACUUM INTO</c> that writes a copy encrypted with <paramref name="pageKey"/> (spec 7.4). Contains the key.
    /// </summary>
    public static string VacuumIntoTarget(string path, ReadOnlySpan<byte> pageKey) =>
        "'file://" + ToUriPath(path).Replace("'", "''") + "?cipher=sqlcipher&legacy=4&key=x''" + Convert.ToHexStringLower(pageKey) + "'''";

    /// <summary>An absolute local path as the path part of a <c>file:</c> URI: forward slashes, a leading slash, %, ?, #, &amp; and space escaped.</summary>
    public static string ToUriPath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.StartsWith("//", StringComparison.Ordinal))
            throw new NotSupportedException("A sealed database cannot live on a network share (UNC path).");
        var sb = new StringBuilder(full.Length + 8);
        if (!full.StartsWith('/')) sb.Append('/');
        foreach (var c in full)
        {
            sb.Append(c switch
            {
                '%' => "%25",
                '?' => "%3F",
                '#' => "%23",
                '&' => "%26",
                ' ' => "%20",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}

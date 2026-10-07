using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class SnapshotService
{
    /// <summary>
    /// Deletes any *.enc file in data/media that has no corresponding row in tbl_media.
    /// Called after every restore (network or join) to reconcile state, and from the startup
    /// sweep (ApiStartupTasks), right after <see cref="ResumeMediaStaging"/>, for whatever a
    /// process kill left behind.
    /// </summary>
    public void CleanupOrphanMediaFiles()
    {
        var registeredIds = RegisteredMediaIds();

        var mediaDir = Path.Combine(_dataPath, "media");
        if (!Directory.Exists(mediaDir)) return;

        var orphansDeleted = 0;
        foreach (var f in Directory.GetFiles(mediaDir, "*.enc"))
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(f);
            if (Guid.TryParse(nameWithoutExt, out var id) && !registeredIds.Contains(id))
            {
                File.Delete(f);
                orphansDeleted++;
            }
        }

        if (orphansDeleted > 0)
            _logger?.LogInformation("Deleted {Count} orphan media files after import", orphansDeleted);
    }

    /// <summary>
    /// A restore commits the database first and swaps <c>media.staging</c> into <c>media</c> afterwards. A process
    /// that died between the two left the restored legacy media (rows without a blob, bytes only in an .enc file) in
    /// <c>media.staging</c>, where nothing reads it: those images answered 404 until someone moved the folder by hand.
    /// At startup, before the orphan sweep, every staged .enc whose id the committed tbl_media names and that
    /// <c>media</c> does not already hold is moved into <c>media</c>. Nothing is overwritten and nothing is deleted:
    /// a staged file that is not needed stays where it is (the next restore clears the folder), and an empty
    /// staging folder is removed. Returns how many files were moved.
    /// </summary>
    public int ResumeMediaStaging()
    {
        var stagingDir = Path.Combine(_dataPath, "media.staging");
        if (!Directory.Exists(stagingDir)) return 0;

        var registeredIds = RegisteredMediaIds();
        var mediaDir = Path.Combine(_dataPath, "media");
        Directory.CreateDirectory(mediaDir);

        var moved = 0;
        var left = 0;
        foreach (var staged in Directory.GetFiles(stagingDir, "*.enc"))
        {
            var name = Path.GetFileName(staged);
            var target = Path.Combine(mediaDir, name);
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(staged), out var id)
                && registeredIds.Contains(id)
                && !File.Exists(target))
            {
                File.Move(staged, target, overwrite: false);
                moved++;
            }
            else
            {
                left++;
            }
        }

        if (left == 0 && Directory.GetFileSystemEntries(stagingDir).Length == 0)
            Directory.Delete(stagingDir);

        if (moved > 0 || left > 0)
            _logger?.LogWarning(
                "Startup: an interrupted restore left media in media.staging; moved {Moved} file(s) the database names into media/, left {Left} in place",
                moved, left);
        return moved;
    }

    private HashSet<Guid> RegisteredMediaIds()
    {
        var registeredIds = new HashSet<Guid>();
        using var conn = _connFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM tbl_media";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            registeredIds.Add(reader.GetGuid(0));
        return registeredIds;
    }

    private static async Task<byte[]> ExtractManifestFromTarGzAsync(string tarGzPath)
    {
        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name == ManifestFileName)
            {
                await using var stream = entry.DataStream!;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }

        throw new InvalidOperationException("Snapshot does not contain manifest.json");
    }

    private static void FilterSecretsFrom(string tempDbPath, IReadOnlyCollection<string>? alsoKeep = null)
    {
        // Pooling=False is load-bearing, not tidiness. Microsoft.Data.Sqlite pools by default, so
        // Dispose only returns the connection to the pool and the native handle stays open on the
        // file. Every caller here deletes or replaces that file immediately afterwards, and on
        // Windows deleting a file with an open handle fails outright — which is why snapshot
        // creation threw IOException locally while Linux CI, where unlink on an open file just
        // works, stayed green. These are one-shot connections to a throwaway file; pooling buys
        // nothing and costs the delete.
        var cs = $"Data Source={tempDbPath};Pooling=False";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var pragmaCmd = conn.CreateCommand();
        // secure_delete: what is deleted below is zeroed as it goes, not just unlinked — the final
        // VACUUM rewrites the file anyway, this keeps the in-between journal free of it too.
        pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF; PRAGMA secure_delete = ON;";
        pragmaCmd.ExecuteNonQuery();

        // Allow-list, not deny-list. Enumerate what is actually IN this database and decide about
        // each table, so a table added by a future migration is stripped by default instead of
        // shipping to peers because nobody remembered to add it to a list of secrets. That default
        // had already leaked several: tbl_remote_api_token (tokens we issued to remote accounts),
        // tbl_search_index_key, tbl_dek_rotation_state — none of them ever named as secret, all of
        // them travelling to every joiner with their contents intact.
        var presentTables = new List<string>();
        using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = listCmd.ExecuteReader();
            while (reader.Read()) presentTables.Add(reader.GetString(0));
        }

        var keep = new HashSet<string>(
            SnapshotTables.Replicated.Concat(SnapshotTables.SchemaMeta).Concat(alsoKeep ?? []),
            StringComparer.OrdinalIgnoreCase);
        var drop = new HashSet<string>(SnapshotTables.StrippedByDropping, StringComparer.OrdinalIgnoreCase);

        // Two tables are neither fully kept nor fully cleared — they are filtered row by row just
        // below. They have to skip the blanket pass, or it empties them first and leaves the
        // careful version with nothing to do.
        var filteredByRow = new HashSet<string>(
            ["tbl_whitelist", "tbl_role"], StringComparer.OrdinalIgnoreCase);

        foreach (var table in presentTables)
        {
            if (keep.Contains(table) || filteredByRow.Contains(table)) continue;

            // FTS shadow tables belong to a virtual table that lives in the receiving node's own
            // schema; they are rebuilt there and are meaningless in transit. Dropping them
            // individually would also corrupt the virtual table they back, so leave them to be
            // emptied along with everything else... except that emptying a shadow table directly
            // is equally unsound. Skip them entirely: they carry a copy of content that is already
            // being replicated, never anything a peer should not have.
            if (table.StartsWith("fts_", StringComparison.OrdinalIgnoreCase)
                || table.Contains("_fts", StringComparison.OrdinalIgnoreCase)) continue;

            using var cmd = conn.CreateCommand();
            // DROP for the tables whose ABSENCE marks this archive as a peer package (see
            // SnapshotTables.StrippedByDropping — RestoreAsync refuses an archive with no key
            // slots, and an empty one would defeat that check). Everything else is emptied so the
            // receiving node keeps a schema it will never recreate on its own.
            cmd.CommandText = drop.Contains(table)
                ? $"DROP TABLE IF EXISTS [{table}]"
                : $"DELETE FROM [{table}]";
            cmd.ExecuteNonQuery();
        }

        // Kept from the old hand-written pass because it is narrower than "empty the table":
        // a joiner legitimately inherits the ACTIVE whitelist so it knows who its peers are, but
        // revoked and pending rows are this node's own history and say nothing useful there.
        using var whitelistCmd = conn.CreateCommand();
        whitelistCmd.CommandText = "DELETE FROM tbl_whitelist WHERE status != 'A'";
        whitelistCmd.ExecuteNonQuery();

        // Likewise narrower: the two seeded system roles stay, because every user row references a
        // role by name and FolderAccessService fails closed on one it cannot resolve. Custom roles
        // are node-local and go.
        using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = "DELETE FROM tbl_role WHERE is_system = 0";
        try { roleCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-009 archive */ }

        Compact(conn);
    }

    /// <summary>
    /// Rewrites the copy from its live rows only, as a single self-contained file: rollback journal
    /// (deleted when done) rather than WAL, so no -wal file holds pages the archive would not show.
    /// </summary>
    private static void Compact(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = DELETE; VACUUM;";
        cmd.ExecuteNonQuery();
    }

    private static void CompactInPlace(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        Compact(conn);
    }

    private static async Task ExtractTarGzAsync(string archivePath, string destDir, long? compressedSize = null)
    {
        // Cap at min(50GB, max(20 × compressed, 50 MB)). The 50 MB floor lets small archives
        // (manifest + side files + small DB) extract normally; the 20× ratio still catches
        // decompression bombs when compressed is large; the 50 GB ceiling is the absolute.
        // Fallback to 50GB cap when compressedSize is null or zero (no metadata available).
        const long absoluteCap = 50_000_000_000;
        const long floor = 50_000_000;
        var maxTotalSize = compressedSize.HasValue && compressedSize.Value > 0
            ? Math.Min(absoluteCap, Math.Max(compressedSize.Value * 20, floor))
            : absoluteCap;
        const long maxFileCount = 1_000_000;
        long totalExtracted = 0;
        long fileCount = 0;

        await using var fs = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile) continue;

            fileCount++;
            if (fileCount > maxFileCount)
                throw new InvalidOperationException($"Tar archive exceeds maximum file count ({maxFileCount})");

            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.Name));
            if (!destPath.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar)
                && destPath != Path.GetFullPath(destDir))
                throw new InvalidOperationException($"Tar entry attempts path traversal: {entry.Name}");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);

            var fi = new FileInfo(destPath);
            totalExtracted += fi.Length;
            if (totalExtracted > maxTotalSize)
                throw new InvalidOperationException($"Tar archive exceeds maximum extracted size ({maxTotalSize / (1024 * 1024)}MB)");
        }
    }

    private static async Task VerifyManifestAsync(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Snapshot manifest.json not found");

        var manifestText = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonDocument.Parse(manifestText);
        var files = manifest.RootElement.GetProperty("files");

        foreach (var prop in files.EnumerateObject())
        {
            var expectedHash = prop.Value.GetString()
                ?? throw new InvalidOperationException($"Invalid hash for {prop.Name}");
            var fullPath = Path.GetFullPath(Path.Combine(dir, prop.Name));
            if (!fullPath.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar))
                throw new InvalidOperationException($"Manifest entry attempts path traversal: {prop.Name}");
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"Snapshot file missing: {prop.Name}");
            var actualHash = await ComputeHashAsync(fullPath);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SHA256 mismatch for {prop.Name}");
        }

        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in files.EnumerateObject())
            manifestFiles.Add(prop.Name.Replace('\\', '/'));
        manifestFiles.Add(ManifestFileName);
        manifestFiles.Add(ManifestFileName + ".sig");
        foreach (var extractedFile in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(dir, extractedFile).Replace('\\', '/');
            if (!manifestFiles.Contains(relativePath))
                throw new InvalidOperationException($"Snapshot tampered: extra unlisted file: {relativePath}");
        }
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}

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
    /// Called after every restore (network or join) to reconcile state. Also called from
    /// the startup sweep in Program.cs to clean up debris left by a process kill that
    /// happened during an in-progress restore (media files copied early but DB transaction
    /// never committed).
    /// </summary>
    public void CleanupOrphanMediaFiles()
    {
        var registeredIds = new HashSet<Guid>();
        using (var conn = _connFactory.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM tbl_media";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                registeredIds.Add(reader.GetGuid(0));
        }

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

    private static void FilterSecretsFrom(string tempDbPath)
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
        pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
        pragmaCmd.ExecuteNonQuery();

        foreach (var table in SecretTables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS [{table}]";
            cmd.ExecuteNonQuery();
        }

        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM tbl_whitelist WHERE status != 'A'";
        delCmd.ExecuteNonQuery();

        // Roles and their folder rules are node-local like tbl_user, so they must not travel to a
        // peer either. They are cleared rather than added to SecretTables because that list DROPS
        // its tables: tbl_migration is not stripped, so a node restored from such an archive would
        // believe migration 009 had run while the tables were gone, and nothing recreates schema
        // after a restore. Emptying keeps the schema intact and still strips the data.
        // The two seeded system roles are kept — every user row references one by name, and
        // FolderAccessService fails closed on a role it cannot resolve.
        using var roleAclCmd = conn.CreateCommand();
        roleAclCmd.CommandText = "DELETE FROM tbl_role_folder_acl_entry";
        try { roleAclCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-009 archive */ }

        using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = "DELETE FROM tbl_role WHERE is_system = 0";
        try { roleCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-009 archive */ }

        // Favorites are per-user and node-local, and tbl_user is DROPPED above — leaving the rows
        // would both ship one node's personal bookmarks to a peer and strand them against user ids
        // that no longer exist there. Emptied rather than dropped for the same schema reason as the
        // role tables above.
        using var favoriteCmd = conn.CreateCommand();
        favoriteCmd.CommandText = "DELETE FROM tbl_favorite";
        try { favoriteCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-011 archive */ }

        // The sync quarantine is this node's own failure bookkeeping: which events it could not
        // apply, and the raw exception text explaining why. Shipping it to a joiner both leaks our
        // internal paths and diagnostics through last_error and pre-poisons the joiner against
        // events it has never actually tried to apply. Emptied rather than dropped for the same
        // schema reason as the role/favorite tables above.
        using var quarantineCmd = conn.CreateCommand();
        quarantineCmd.CommandText = "DELETE FROM tbl_sync_quarantine";
        try { quarantineCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-013 archive */ }

        // L10: tbl_remote_account holds THIS node's wrapped bearer tokens for OTHER people's nodes
        // (remote-subscription "read-only mirror" feature) — encrypted_token is wrapped with our
        // own master DEK, so a joining node that later unlocks the vault (which it's fully trusted
        // with) could otherwise decrypt and use credentials to accounts on completely unrelated
        // third-party nodes it was never given access to. Emptied rather than dropped — same schema
        // reason as the role/favorite tables above: nothing in the join flow recreates this table,
        // so DROP TABLE would leave a joiner's schema silently missing it forever. Child rows in
        // tbl_remote_subscription (mount path, folder path, sync cursor — no credentials, but still
        // node-local config pointing at a relationship the joiner shouldn't inherit) are deleted
        // first since FK enforcement is OFF for this whole method (see PRAGMA above) and won't
        // cascade the delete on its own.
        using var remoteSubCmd = conn.CreateCommand();
        remoteSubCmd.CommandText = "DELETE FROM tbl_remote_subscription";
        try { remoteSubCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-existing-feature archive */ }

        using var remoteAccountCmd = conn.CreateCommand();
        remoteAccountCmd.CommandText = "DELETE FROM tbl_remote_account";
        try { remoteAccountCmd.ExecuteNonQuery(); } catch (SqliteException) { /* pre-existing-feature archive */ }

        using var vacuumCmd = conn.CreateCommand();
        vacuumCmd.CommandText = "VACUUM";
        vacuumCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Extracts a snapshot archive, strips secret tables (identity, key slots, users, sessions, sync state),
    /// and repackages it. Used when distributing a network-wide restore so that the originator's identity
    /// and DEK wrapping never reach peer disks.
    /// </summary>
    public async Task CreateFilteredVariantAsync(string sourceArchivePath, string destinationArchivePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-filter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await ExtractTarGzAsync(sourceArchivePath, tempDir, new FileInfo(sourceArchivePath).Length);

            var dbPath = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(dbPath))
                throw new InvalidOperationException($"Source archive does not contain {DbFileName}");

            await DecryptDbIfNeededAsync(dbPath);

            FilterSecretsFrom(dbPath);

            if (_sessionService is { IsUnlocked: true })
            {
                var reEncDek = _sessionService.GetMasterDek();
                try
                {
                    await EncryptDbFileAsync(dbPath, reEncDek);
                }
                finally
                {
                    Array.Clear(reEncDek);
                }
            }

            var mediaDir = Path.Combine(tempDir, "media");
            var mediaFiles = Directory.Exists(mediaDir)
                ? Directory.GetFiles(mediaDir, "*.enc")
                : Array.Empty<string>();

            var manifestPath = Path.Combine(tempDir, ManifestFileName);
            byte[]? manifestBytes = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath) : null;

            await using var fs = File.Create(destinationArchivePath);
            await using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax);

            await using (var dbStream = File.OpenRead(dbPath))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName)
                {
                    DataStream = dbStream
                });
            }

            foreach (var encFile in mediaFiles)
            {
                await using var mediaStream = File.OpenRead(encFile);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"media/{Path.GetFileName(encFile)}")
                {
                    DataStream = mediaStream
                });
            }

            if (manifestBytes != null)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName)
                {
                    DataStream = new MemoryStream(manifestBytes)
                });
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
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

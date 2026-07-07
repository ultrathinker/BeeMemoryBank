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
        var cs = $"Data Source={tempDbPath}";
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

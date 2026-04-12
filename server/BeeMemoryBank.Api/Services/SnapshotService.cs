using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Manages database snapshots via HTTP API.
/// Snapshots are stored in {dataPath}/snapshots/.
/// </summary>
public class SnapshotService(string dataPath, DbConnectionFactory connFactory)
{
    private const string DbFileName = "beememorybank.db";
    private const string ManifestFileName = "manifest.json";

    private string SnapshotsDir => Path.Combine(dataPath, "snapshots");

    public List<SnapshotInfo> List()
    {
        Directory.CreateDirectory(SnapshotsDir);
        return Directory.GetFiles(SnapshotsDir, "*.tar.gz")
            .Select(f => new SnapshotInfo(
                Path.GetFileName(f),
                new FileInfo(f).Length,
                File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    public async Task<SnapshotInfo> CreateAsync()
    {
        Directory.CreateDirectory(SnapshotsDir);

        var tempDb = Path.GetTempFileName();
        try
        {
            // VACUUM INTO — consistent DB copy without locks
            using (var conn = (SqliteConnection)connFactory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                // AUDIT NOTE: String interpolation is safe — tempDb comes from Path.GetTempFileName()
                // (system-generated, no user input). VACUUM INTO does not support parameterized queries.
                cmd.CommandText = $"VACUUM INTO '{tempDb}'";
                cmd.ExecuteNonQuery();
            }

            var allFiles = new Dictionary<string, string>();

            var dbHash = await ComputeHashAsync(tempDb);
            allFiles[DbFileName] = dbHash;

            // Collect media file hashes
            var mediaDir = Path.Combine(dataPath, "media");
            var mediaFiles = new List<string>();
            if (Directory.Exists(mediaDir))
            {
                foreach (var encFile in Directory.GetFiles(mediaDir, "*.enc"))
                {
                    var relativePath = $"media/{Path.GetFileName(encFile)}";
                    allFiles[relativePath] = await ComputeHashAsync(encFile);
                    mediaFiles.Add(encFile);
                }
            }

            var manifest = new
            {
                version = mediaFiles.Count > 0 ? 2 : 1,
                createdAt = DateTime.UtcNow.ToString("o"),
                files = allFiles
            };
            var manifestBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"bmb-snapshot-{timestamp}.tar.gz";
            var filePath = Path.Combine(SnapshotsDir, fileName);

            await using (var fs = File.Create(filePath))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName)
                {
                    DataStream = File.OpenRead(tempDb)
                });

                // Include encrypted media files
                foreach (var encFile in mediaFiles)
                {
                    var relativePath = $"media/{Path.GetFileName(encFile)}";
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, relativePath)
                    {
                        DataStream = File.OpenRead(encFile)
                    });
                }

                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName)
                {
                    DataStream = new MemoryStream(manifestBytes)
                });
            }

            var fi = new FileInfo(filePath);
            return new SnapshotInfo(fileName, fi.Length, fi.LastWriteTimeUtc);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    public bool Delete(string fileName)
    {
        // Prevent path traversal
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return false;

        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath)) return false;

        File.Delete(filePath);
        return true;
    }

    public string GetSnapshotPath(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid snapshot file name");
        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Snapshot {safeName} not found");
        return filePath;
    }

    public async Task RestoreAsync(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid snapshot file name");

        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Snapshot {safeName} not found");

        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            await ExtractTarGzAsync(filePath, tempDir);

            await VerifyManifestAsync(tempDir);

            var dbPath = Path.Combine(dataPath, "beememorybank.db");

            // AUDIT NOTE: ClearAllPools releases pooled connections but not active in-flight ones.
            // MaintenanceMiddleware blocks new requests before we get here, and the 200ms delay
            // gives in-flight requests time to complete. On Linux, File.Copy replaces the inode
            // so old file handles remain valid (stale data) but won't corrupt the new file.
            // Acceptable for a personal KB with low concurrency.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(200);

            var extractedDb = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Snapshot does not contain a database file");
            File.Copy(extractedDb, dbPath, overwrite: true);

            var mediaDir = Path.Combine(dataPath, "media");
            if (Directory.Exists(mediaDir))
            {
                foreach (var f in Directory.GetFiles(mediaDir, "*.enc"))
                    File.Delete(f);
            }
            Directory.CreateDirectory(mediaDir);

            var extractedMedia = Path.Combine(tempDir, "media");
            if (Directory.Exists(extractedMedia))
            {
                foreach (var f in Directory.GetFiles(extractedMedia, "*.enc"))
                    File.Copy(f, Path.Combine(mediaDir, Path.GetFileName(f)), overwrite: true);
            }

            using (var conn = connFactory.CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM tbl_sync_push_position";
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task ExtractTarGzAsync(string archivePath, string destDir)
    {
        await using var fs = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile) continue;

            // Path traversal protection (zip-slip)
            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.Name));
            if (!destPath.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar)
                && destPath != Path.GetFullPath(destDir))
                throw new InvalidOperationException($"Tar entry attempts path traversal: {entry.Name}");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
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
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}

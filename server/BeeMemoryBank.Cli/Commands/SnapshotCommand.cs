using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// Commands for creating and restoring snapshots.
/// A snapshot is a tar.gz archive containing a database copy and a manifest.
/// </summary>
public static class SnapshotCommand
{
    private const string DbFileName = "beememorybank.db";
    private const string ManifestFileName = "manifest.json";
    private const int ManifestVersion = 1;

    public static async Task<int> HandleCreateAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;

        var dbPath = Path.Combine(dataPath, DbFileName);
        if (!File.Exists(dbPath))
        {
            await output.WriteLineAsync("Error: database not found. Run 'bmb init' first.");
            return 1;
        }

        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync();

        // Create a VACUUM copy of the database for a consistent snapshot
        var tempDb = Path.GetTempFileName();
        try
        {
            var connFactory = services.GetRequiredService<DbConnectionFactory>();
            using (var conn = (SqliteConnection)connFactory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{tempDb}'";
                cmd.ExecuteNonQuery();
            }

            // Compute SHA256 of the DB copy
            var dbHash = await ComputeFileHashAsync(tempDb);

            // Create manifest
            var manifest = new
            {
                version = ManifestVersion,
                createdAt = DateTime.UtcNow.ToString("o"),
                nodeId = identity?.NodeId.ToString() ?? "",
                displayName = identity?.DisplayName ?? "",
                files = new Dictionary<string, string>
                {
                    [DbFileName] = dbHash
                }
            };
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            // Generate snapshot file name. Place under {dataPath}/snapshots/ so
            // `bmb snapshot restore-standalone <name>` can find it by file name —
            // the running API server reads from that directory.
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var snapshotName = $"bmb-snapshot-{timestamp}.tar.gz";
            var snapshotsDir = Path.Combine(dataPath, "snapshots");
            Directory.CreateDirectory(snapshotsDir);
            var snapshotPath = Path.Combine(snapshotsDir, snapshotName);

            // Create tar.gz
            await using (var fileStream = File.Create(snapshotPath))
            await using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                // Add DB file
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName)
                {
                    DataStream = File.OpenRead(tempDb)
                });

                // Add manifest
                var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName)
                {
                    DataStream = new MemoryStream(manifestBytes)
                });
            }

            await output.WriteLineAsync($"Snapshot created: {snapshotPath}");
            await output.WriteLineAsync($"  Node: {identity?.DisplayName ?? "unknown"} ({identity?.NodeId})");
            await output.WriteLineAsync($"  SHA256(db): {dbHash}");
            return 0;
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    public static async Task<int> HandleRestoreAsync(string dataPath, string snapshotPath, TextWriter? output = null)
    {
        output ??= Console.Out;

        if (!File.Exists(snapshotPath))
        {
            await output.WriteLineAsync($"Error: snapshot file not found: {snapshotPath}");
            return 1;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "bmb_restore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Extract tar.gz
            await using (var fileStream = File.OpenRead(snapshotPath))
            await using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, tempDir, overwriteFiles: true);
            }

            var extractedDb = Path.Combine(tempDir, DbFileName);
            var extractedManifest = Path.Combine(tempDir, ManifestFileName);

            if (!File.Exists(extractedDb) || !File.Exists(extractedManifest))
            {
                await output.WriteLineAsync("Error: snapshot is corrupted — required files are missing.");
                return 1;
            }

            // Read and validate the manifest
            var manifestJson = await File.ReadAllTextAsync(extractedManifest);
            var manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson);

            var expectedHash = manifest.GetProperty("files").GetProperty(DbFileName).GetString()!;
            var actualHash = await ComputeFileHashAsync(extractedDb);

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync($"Error: database checksum mismatch.");
                await output.WriteLineAsync($"  Expected: {expectedHash}");
                await output.WriteLineAsync($"  Actual:   {actualHash}");
                return 1;
            }

            // Restore database
            Directory.CreateDirectory(dataPath);
            var targetDb = Path.Combine(dataPath, DbFileName);

            // Create a backup of the existing database if it exists
            if (File.Exists(targetDb))
            {
                var backupPath = targetDb + ".bak";
                File.Copy(targetDb, backupPath, overwrite: true);
                await output.WriteLineAsync($"  Existing database backed up: {backupPath}");
            }

            File.Copy(extractedDb, targetDb, overwrite: true);

            // Restore media files if present in snapshot
            var tempMediaDir = Path.Combine(tempDir, "media");
            if (Directory.Exists(tempMediaDir))
            {
                var targetMediaDir = Path.Combine(dataPath, "media");
                Directory.CreateDirectory(targetMediaDir);

                var mediaFiles = Directory.GetFiles(tempMediaDir, "*.enc");
                foreach (var file in mediaFiles)
                {
                    var targetFile = Path.Combine(targetMediaDir, Path.GetFileName(file));
                    File.Copy(file, targetFile, overwrite: true);

                    // Validate hash if manifest v2
                    if (manifest.TryGetProperty("version", out var ver) && ver.GetInt32() >= 2)
                    {
                        var relativePath = $"media/{Path.GetFileName(file)}";
                        if (manifest.GetProperty("files").TryGetProperty(relativePath, out var expectedMediaHash))
                        {
                            var actualMediaHash = await ComputeFileHashAsync(targetFile);
                            if (!string.Equals(expectedMediaHash.GetString(), actualMediaHash, StringComparison.OrdinalIgnoreCase))
                            {
                                await output.WriteLineAsync($"  Warning: checksum mismatch for {relativePath}");
                            }
                        }
                    }
                }
                await output.WriteLineAsync($"  Media files restored: {mediaFiles.Length}");
            }

            var nodeId = manifest.TryGetProperty("nodeId", out var nid) ? nid.GetString() : "unknown";
            var displayName = manifest.TryGetProperty("displayName", out var dn) ? dn.GetString() : "unknown";
            var createdAt = manifest.TryGetProperty("createdAt", out var ca) ? ca.GetString() : "unknown";

            await output.WriteLineAsync($"Snapshot restored:");
            await output.WriteLineAsync($"  Node: {displayName} ({nodeId})");
            await output.WriteLineAsync($"  Snapshot date: {createdAt}");
            await output.WriteLineAsync($"  Database: {targetDb}");
            return 0;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error during restore: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static HttpClient CreateClient(string? dataPath = null)
    {
        var url = Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://localhost:5300";
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");

        // Auto-load internal key from data dir if env var unset.
        if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(dataPath))
        {
            var keyFile = Path.Combine(dataPath, ".internal-key");
            if (File.Exists(keyFile))
            {
                try { key = File.ReadAllText(keyFile).Trim(); } catch { }
            }
        }

        var http = new HttpClient { BaseAddress = new Uri(url) };
        if (!string.IsNullOrEmpty(key))
            http.DefaultRequestHeaders.Add("X-Internal-Key", key);
        // Snapshot/init endpoints require superadmin role; CLI runs locally as admin
        // so we set this header by default. Server still enforces internal-key auth.
        http.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return http;
    }

    public static async Task<int> HandleListAsync(string dataPath)
    {
        try
        {
            using var http = CreateClient(dataPath);
            var resp = await http.GetAsync("/api/snapshots");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {resp.StatusCode}");
                return 1;
            }
            var json = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HandleRestoreNetworkAsync(string dataPath, string fileIdOrName)
    {
        try
        {
            using var http = CreateClient(dataPath);
            Guid fileId = Guid.Empty;
            Guid.TryParse(fileIdOrName, out fileId);
            
            var req = new
            {
                snapshotFileId = fileId,
                mode = 1 // NetworkWide
            };
            
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/api/snapshots/restore-network", content);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {await resp.Content.ReadAsStringAsync()}");
                return 1;
            }
            Console.WriteLine("Network restore initiated successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HandleRestoreStandaloneAsync(string dataPath, string fileName)
    {
        try
        {
            Console.Write("Enter Master Password: ");
            var pwd = Console.ReadLine();

            using var http = CreateClient(dataPath);
            var req = new
            {
                fileName = fileName,
                masterPassword = pwd,
                createBackupFirst = true,
                standaloneMode = true
            };

            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            Console.WriteLine("Initiating standalone restore...");
            var resp = await http.PostAsync("/api/snapshots/restore", content);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {await resp.Content.ReadAsStringAsync()}");
                return 1;
            }
            Console.WriteLine("Standalone restore completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HandleRestoreStatusAsync(string dataPath)
    {
        try
        {
            using var http = CreateClient(dataPath);
            var resp = await http.GetAsync("/api/snapshots/restore/progress");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {resp.StatusCode} - {await resp.Content.ReadAsStringAsync()}");
                return 1;
            }
            var json = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<string> ComputeFileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}

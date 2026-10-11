using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
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

    public static async Task<int> HandleCreateAsync(string dataPath, TextWriter? output = null, Action<string>? afterStagingWork = null)
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

        // A sealed vault (docs/design/step-b-spec.md 8.3): the snapshot's database entry is a BMBDB2 container built from the page codec, never
        // a plain SQLite file. Everything it needs comes from the vault gate that opened the directory (the master password).
        if (services.GetService<IVaultGate>() is { IsSealed: true } gate)
        {
            var session = services.GetRequiredService<SessionService>();
            var masterDek = session.IsUnlocked ? session.GetMasterDek() : null;
            try
            {
                return await HandleSealedCreateAsync(dataPath, gate, services.GetService<IVaultKeySource>(),
                    services.GetRequiredService<DbConnectionFactory>(), masterDek, identity, output);
            }
            finally
            {
                if (masterDek is not null) CryptographicOperations.ZeroMemory(masterDek);
            }
        }

        // Create a VACUUM copy of the database for a consistent snapshot - staged in the data folder like the node's own
        // snapshots (SnapshotStaging), owner-only, not in the OS temp folder. The node's next start removes it if this
        // command dies before its finally; the CLI itself never sweeps, since the node may be running a snapshot.
        var tempDb = SnapshotStaging.NewFile(Path.GetDirectoryName(services.GetRequiredService<BeeMemoryBank.Core.Services.MediaStorageOptions>().MediaDir)!);
        string dbHash, snapshotPath;
        var copyRemoved = false;
        try
        {
            var connFactory = services.GetRequiredService<DbConnectionFactory>();
            using (var conn = (SqliteConnection)connFactory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{tempDb.Replace("'", "''")}'";
                cmd.ExecuteNonQuery();
            }

            // Compute SHA256 of the DB copy
            dbHash = await ComputeFileHashAsync(tempDb);

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
            snapshotPath = Path.Combine(snapshotsDir, snapshotName);

            // Create tar.gz
            await using (var fileStream = File.Create(snapshotPath))
            await using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                // Add DB file. The stream is scoped, not handed to the entry inline: TarWriter does
                // not take ownership of DataStream, so an inline File.OpenRead leaked the handle
                // and the `finally` below could not delete tempDb. On Linux that is invisible —
                // unlink succeeds on an open file — which is why it only ever failed on Windows,
                // and why CI stayed green while three CLI tests failed locally.
                {
                    await using var dbStream = File.OpenRead(tempDb);
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName)
                    {
                        DataStream = dbStream
                    });
                }

                // Add manifest
                var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName)
                {
                    DataStream = new MemoryStream(manifestBytes)
                });
            }

        }
        finally
        {
            // The working copy goes with its SQLite sidecars before the snapshot is reported: one that stays fails the command.
            afterStagingWork?.Invoke(tempDb);
            copyRemoved = SqliteStaging.TryCleanup(tempDb);
        }

        if (!copyRemoved)
        {
            RemoveUnreported(snapshotPath);
            await output.WriteLineAsync($"Error: {WorkingCopyNotRemoved}");
            return 1;
        }
        await output.WriteLineAsync($"Snapshot created: {snapshotPath}");
        await output.WriteLineAsync($"  Node: {identity?.DisplayName ?? "unknown"} ({identity?.NodeId})");
        await output.WriteLineAsync($"  SHA256(db): {dbHash}");
        return 0;
    }

    /// <summary>What a snapshot writer reports when its working copy stays on the disk: the snapshot is not made, as far as anyone may rely on it.</summary>
    private const string WorkingCopyNotRemoved =
        "The working copy of the snapshot could not be removed; node repair is required. The snapshot is not reported as made.";

    /// <summary>The archive of a snapshot that is not reported as made goes too: a leftover file is no snapshot anyone asked for.</summary>
    private static void RemoveUnreported(string archive)
    {
        try { File.Delete(archive); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover file is no snapshot */ }
    }

    /// <summary>What a verb that needs the master password answers on a sealed directory it could not open (docs/design/step-b-spec.md 5.9).</summary>
    public const string SealedNeedsPassword = "This vault is encrypted: give the master password, or use the running node.";

    /// <summary>The archive <see cref="CreateSealedSnapshotAsync"/> wrote and the SHA-256 of its database entry (the BMBDB2 container).</summary>
    public sealed record SealedSnapshotResult(string Path, string DbHash);

    /// <summary>
    /// <c>bmb snapshot create</c> on a sealed vault whose gate is open (the directory was opened with the master password, spec 5.9). Answers
    /// <see cref="SealedNeedsPassword"/> when the vault is not open or the session holds no master DEK; never writes a plain database.
    /// </summary>
    public static async Task<int> HandleSealedCreateAsync(string dataPath, IVaultGate gate, IVaultKeySource? keys, DbConnectionFactory vault,
        byte[]? masterDek, NodeIdentity? identity, TextWriter output)
    {
        if (!gate.IsUsable || keys is not { IsOpen: true } || masterDek is null)
        {
            await output.WriteLineAsync(SealedNeedsPassword);
            return 1;
        }

        SealedSnapshotResult result;
        try
        {
            result = await CreateSealedSnapshotAsync(dataPath, vault, keys, masterDek, identity?.NodeId.ToString() ?? "", identity?.DisplayName ?? "");
        }
        catch (IOException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
        await output.WriteLineAsync($"Snapshot created: {result.Path}");
        await output.WriteLineAsync($"  Node: {identity?.DisplayName ?? "unknown"} ({identity?.NodeId})");
        await output.WriteLineAsync($"  SHA256(db): {result.DbHash}");
        return 0;
    }

    /// <summary>
    /// A snapshot of a sealed vault as the node itself makes it (spec 8.3): <c>VACUUM INTO</c> a staging copy that inherits the page key,
    /// the plain image read from it page by page (<see cref="PlainImageReader"/>) straight into a BMBDB2 container under the master DEK,
    /// and a tar.gz of the container and a manifest with <c>dbEncrypted: true</c>. No plain SQLite file exists at any moment; the staging
    /// copy is encrypted and is wiped at the end.
    /// </summary>
    public static async Task<SealedSnapshotResult> CreateSealedSnapshotAsync(string dataPath, DbConnectionFactory vault, IVaultKeySource keys,
        byte[] masterDek, string nodeId, string displayName, CancellationToken ct = default, Action<string>? afterStagingWork = null)
    {
        var staging = SnapshotStaging.NewFile(dataPath);
        string? madeArchive = null;
        try
        {
            using (var conn = (SqliteConnection)vault.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{staging.Replace("'", "''")}'";
                cmd.ExecuteNonQuery();
            }

            byte[] container;
            var pageKey = keys.GetPageKey(DbPurposes.Vault);
            try
            {
                await using var image = PlainImageReader.Open(staging, pageKey);
                container = await Bmbdb2Container.EncryptAsync(image, image.Length, masterDek, ct);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pageKey);
            }

            var dbHash = Convert.ToHexStringLower(SHA256.HashData(container));
            var manifest = new
            {
                version = ManifestVersion,
                createdAt = DateTime.UtcNow.ToString("o"),
                nodeId,
                displayName,
                files = new Dictionary<string, string> { [DbFileName] = dbHash },
                snapshotFormatVersion = 4,
                dbEncrypted = true,
            };
            var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var snapshotsDir = Path.Combine(dataPath, "snapshots");
            Directory.CreateDirectory(snapshotsDir);
            var snapshotPath = Path.Combine(snapshotsDir, $"bmb-snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.gz");
            await using (var fileStream = File.Create(snapshotPath))
            await using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName) { DataStream = new MemoryStream(container) }, ct);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName) { DataStream = new MemoryStream(manifestBytes) }, ct);
            }
            madeArchive = snapshotPath;
            return new SealedSnapshotResult(snapshotPath, dbHash);
        }
        finally
        {
            // Encrypted already, but it is a key-less image of the vault: wiped, not just deleted, with its SQLite sidecars. One that stays
            // fails the snapshot, which is not reported as made: its archive goes too.
            afterStagingWork?.Invoke(staging);
            var gone = SqliteStaging.TryCleanup(staging, path => SecureFileWipe.TryWipe(path));
            if (!gone && madeArchive is not null)
            {
                RemoveUnreported(madeArchive);
                throw new IOException(WorkingCopyNotRemoved);
            }
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

        // The extraction is the vault in clear: in the data folder (SnapshotStaging), owner-only, never in the OS temp folder.
        var tempDir = SnapshotStaging.NewDirectory(dataPath);
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

            // A snapshot of a sealed vault (or any snapshot with an encrypted database) carries a BMBDB container, not a SQLite file:
            // copying it over the database would destroy the vault. The node's restore opens it with the master password.
            if (Bmbdb2Container.IsContainer(await ReadHeadAsync(extractedDb)))
            {
                await output.WriteLineAsync("Error: this snapshot's database is encrypted. Restore it with 'bmb snapshot restore-standalone <file name>' on the running node.");
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

            // The extraction is the vault in clear: it is gone before the restore is reported. One that stays fails the restore.
            if (!SnapshotStaging.TryCleanupDirectory(tempDir))
            {
                await output.WriteLineAsync("Error: the extracted copy of the snapshot could not be removed; node repair is required. The restore is not reported as done.");
                return 1;
            }

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
            SnapshotStaging.TryCleanupDirectory(tempDir);
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
            // An uploaded snapshot is found by the id in its name, one this node made by its file name.
            var isId = Guid.TryParse(fileIdOrName, out var fileId);

            var req = new
            {
                snapshotFileId = isId ? fileId : Guid.Empty,
                fileName = isId ? null : fileIdOrName,
                // By name: 1 is the numeric value of Standalone, which the endpoint refuses ("Use /restore for standalone").
                mode = "NetworkWide"
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

    private static async Task<byte[]> ReadHeadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var head = new byte[64];
        var n = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
        return head[..n];
    }

    private static async Task<string> ComputeFileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}

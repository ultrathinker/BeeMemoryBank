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

        // A node whose database is not encrypted makes no snapshot here (BMB-306): the archive would hold the database in the open, and no restore takes
        // one ("a restore never produces an unencrypted node"). Admin > Snapshots makes an encrypted snapshot of such a node; encrypting the node first
        // (bmb vault seal) lets this command make one too.
        await output.WriteLineAsync(
            "Error: this node's database is not encrypted, and a snapshot made here would hold it in the open - a restore does not take one. " +
            "Make the snapshot in Admin, Snapshots (it is encrypted), or encrypt this node first (bmb vault seal).");
        return 1;
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
        byte[]? masterDek, NodeIdentity? identity, TextWriter output, long? freeMemoryBytes = null)
    {
        if (!gate.IsUsable || keys is not { IsOpen: true } || masterDek is null)
        {
            await output.WriteLineAsync(SealedNeedsPassword);
            return 1;
        }

        SealedSnapshotResult result;
        try
        {
            result = await CreateSealedSnapshotAsync(dataPath, vault, keys, masterDek, identity?.NodeId.ToString() ?? "", identity?.DisplayName ?? "",
                freeMemoryBytes: freeMemoryBytes);
        }
        catch (Exception ex) when (ex is IOException or NotEnoughMemoryException)
        {
            // NotEnoughMemoryException names the memory limit that would be enough (BMB-304).
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
        byte[] masterDek, string nodeId, string displayName, CancellationToken ct = default, Action<string>? afterStagingWork = null, Func<DateTime>? nameClock = null,
        long? freeMemoryBytes = null, Func<Stream, Stream>? wrapArchive = null)
    {
        // The container is one AES-GCM message held in memory as one buffer (BMB-304): ask for that room before the working copy is made, and name the
        // memory limit that would be enough (the codec asks again for the exact size of the image, so a wrong guess here only costs the early answer).
        // The size is the live image (VACUUM INTO leaves the free pages out), not the length of the file.
        var vaultFile = Path.Combine(dataPath, DbFileName);
        if (File.Exists(vaultFile))
        {
            long size;
            using (var sizeConn = vault.CreateConnection()) size = Bmbdb2Container.LiveImageBytes(sizeConn, new FileInfo(vaultFile).Length);
            MemoryBudget.RequireFree(size + Bmbdb2Container.OverheadV2 + MemoryBudget.Slack,
                $"Making the snapshot of a {Math.Max(1, size / (1024 * 1024))} MB database", freeMemoryBytes);
        }

        var staging = SnapshotStaging.NewFile(dataPath);
        string? madeArchive = null;
        string? tempArchive = null;
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
                container = await Bmbdb2Container.EncryptAsync(image, image.Length, masterDek, ct, freeMemoryBytes);
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

            var snapshotsDir = Path.Combine(dataPath, SnapshotFileNames.SnapshotsDir);
            Directory.CreateDirectory(snapshotsDir);
            var snapshotPath = Path.Combine(snapshotsDir, SnapshotFileNames.Unique(dataPath, snapshotsDir, nameClock?.Invoke() ?? DateTime.UtcNow));
            // Written under a temp name, owner-only from the first byte, and given its name only when it is whole: a full volume or a cancellation leaves no
            // half-written *.tar.gz, and the finally below removes the temp file (BMB-304 fix round).
            tempArchive = snapshotPath + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + ".tmp";
            await using (var fileStream = OwnerOnlyFile.CreateNew(tempArchive))
            await using (var gzip = new GZipStream(wrapArchive?.Invoke(fileStream) ?? fileStream, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName) { DataStream = new MemoryStream(container) }, ct);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName) { DataStream = new MemoryStream(manifestBytes) }, ct);
            }
            ct.ThrowIfCancellationRequested();
            OwnerOnlyFile.MoveNoReplace(tempArchive, snapshotPath);
            tempArchive = null;
            madeArchive = snapshotPath;
            return new SealedSnapshotResult(snapshotPath, dbHash);
        }
        finally
        {
            // Encrypted already, but it is a key-less image of the vault: wiped, not just deleted, with its SQLite sidecars. One that stays
            // fails the snapshot, which is not reported as made: its archive goes too.
            afterStagingWork?.Invoke(staging);
            if (tempArchive is not null) RemoveUnreported(tempArchive);
            var gone = SqliteStaging.TryCleanup(staging, path => SecureFileWipe.TryWipe(path));
            if (!gone && madeArchive is not null)
            {
                RemoveUnreported(madeArchive);
                throw new IOException(WorkingCopyNotRemoved);
            }
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

}

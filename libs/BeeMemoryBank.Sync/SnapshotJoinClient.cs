using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public class SnapshotJoinClient
{
    private const string ManifestFileName = "manifest.json";

    private static readonly string[] ImportTables =
    [
        "tbl_folder", "tbl_article", "tbl_article_body", "tbl_concept_tag",
        "tbl_article_concept_tag", "tbl_concept_tag_edge", "tbl_media",
        "tbl_tombstone", "tbl_conflict_version", "tbl_projection_matrix"
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly DbConnectionFactory _connFactory;
    private readonly string _dataDir;
    private readonly ILogger<SnapshotJoinClient> _logger;

    public SnapshotJoinClient(
        HttpClient http,
        DbConnectionFactory connFactory,
        string dataDir,
        ILogger<SnapshotJoinClient> logger)
    {
        _http = http;
        _connFactory = connFactory;
        _dataDir = dataDir;
        _logger = logger;
    }

    public async Task<(long CpSeq, long LamportTs)> DownloadAndImportAsync(
        string remoteUrl,
        Guid localNodeId,
        byte[] localPrivateKey,
        byte[] producerPublicKey,
        CancellationToken ct = default)
    {
        remoteUrl = remoteUrl.TrimEnd('/');

        _logger.LogInformation("Requesting auth challenge from {Remote}", remoteUrl);
        var challengeResp = await _http.PostAsync($"{remoteUrl}/api/sync/challenge", null, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challenge = await challengeResp.Content.ReadFromJsonAsync<ChallengeResponseDto>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty challenge response");

        var challengeBytes = Convert.FromBase64String(challenge.Challenge);
        var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
        var challengePayload = domainTag.Concat(challengeBytes).ToArray();
        var challengeSig = Ed25519Signer.Sign(localPrivateKey, challengePayload);

        var authResp = await _http.PostAsJsonAsync($"{remoteUrl}/api/sync/authenticate",
            new
            {
                NodeId = localNodeId,
                ChallengeB64 = challenge.Challenge,
                SignatureB64 = Convert.ToBase64String(challengeSig)
            }, JsonOpts, ct);
        authResp.EnsureSuccessStatusCode();
        var authToken = (await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts, ct))?.Token
            ?? throw new InvalidOperationException("Empty auth response");

        _logger.LogInformation("Downloading snapshot from {Remote}", remoteUrl);
        using var snapReq = new HttpRequestMessage(HttpMethod.Get, $"{remoteUrl}/api/sync/snapshot/for-join");
        snapReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        var snapResp = await _http.SendAsync(snapReq, ct);
        snapResp.EnsureSuccessStatusCode();

        var signatureB64 = snapResp.Headers.GetValues("X-BMB-Snapshot-Signature").FirstOrDefault()
            ?? throw new InvalidOperationException("Missing X-BMB-Snapshot-Signature header");
        var signature = Convert.FromBase64String(signatureB64);

        var tempTarGz = Path.Combine(Path.GetTempPath(), $"bmb-mobile-join-{Guid.NewGuid():N}.tar.gz");
        try
        {
            await using (var fs = File.Create(tempTarGz))
                await snapResp.Content.CopyToAsync(fs, ct);

            return await ImportSnapshotFileAsync(tempTarGz, signature, producerPublicKey, ct);
        }
        finally
        {
            if (File.Exists(tempTarGz)) File.Delete(tempTarGz);
        }
    }

    public async Task<(long CpSeq, long LamportTs)> ImportSnapshotFileAsync(
        string tarGzPath, byte[] signature, byte[] producerPublicKey, CancellationToken ct = default)
    {
        var manifestBytes = await ExtractManifestFromTarGzAsync(tarGzPath, ct);

        var payload = await ComputeSignaturePayloadAsync(manifestBytes, tarGzPath, ct);

        if (!Ed25519Signer.Verify(producerPublicKey, payload, signature))
            throw new InvalidOperationException("Snapshot signature verification failed");

        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var version = manifestDoc.RootElement.GetProperty("version").GetInt32();
        if (version < 3)
            throw new InvalidOperationException($"Snapshot manifest version {version} is too old; sync-export requires v3+");

        var snapMigrationVersion = manifestDoc.RootElement.TryGetProperty("migrationVersion", out var mv) ? mv.GetInt32() : -1;
        using (var mvcConn = (SqliteConnection)_connFactory.CreateConnection())
        using (var mvcCmd = mvcConn.CreateCommand())
        {
            mvcCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM tbl_migration";
            var localMigrationVersion = Convert.ToInt32(mvcCmd.ExecuteScalar());
            if (snapMigrationVersion > localMigrationVersion)
                throw new InvalidOperationException(
                    $"Snapshot was produced with schema version {snapMigrationVersion}, but this node is on version {localMigrationVersion}. Upgrade this node first.");
        }

        var cpSeq = manifestDoc.RootElement.GetProperty("cpSequenceNum").GetInt64();
        var lamportTs = manifestDoc.RootElement.GetProperty("lamportTsAtCp").GetInt64();

        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-mobile-join-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await ExtractTarGzAsync(tarGzPath, tempDir, ct);
            await VerifyManifestFileHashesAsync(tempDir);

            var extractedDb = Path.Combine(tempDir, "beememorybank.db");
            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Snapshot does not contain a database file");

            ImportTablesFromAttachedDb(extractedDb);

            var mediaSource = Path.Combine(tempDir, "media");
            var mediaTarget = Path.Combine(_dataDir, "media");
            Directory.CreateDirectory(mediaTarget);
            if (Directory.Exists(mediaSource))
            {
                foreach (var f in Directory.GetFiles(mediaSource, "*.enc"))
                    File.Copy(f, Path.Combine(mediaTarget, Path.GetFileName(f)), overwrite: true);
            }

            CleanupOrphanMediaFiles();

            return (cpSeq, lamportTs);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private void ImportTablesFromAttachedDb(string extractedDbPath)
    {
        using var conn = (SqliteConnection)_connFactory.CreateConnection();

        using (var fkOff = conn.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF";
            fkOff.ExecuteNonQuery();
        }

        // ATTACH DATABASE doesn't accept bound parameters for the path, so we have to
        // interpolate. SQLite single-quote literals are escaped by doubling the quote;
        // mirror SnapshotService.RestoreForJoinAsync. extractedDbPath comes from
        // Path.GetTempPath()/Path.GetTempFileName() — typically safe, but $TMPDIR is
        // user-controlled and could contain a quote.
        var safeAttachPath = extractedDbPath.Replace("'", "''");
        using var attachCmd = conn.CreateCommand();
        attachCmd.CommandText = $"ATTACH DATABASE '{safeAttachPath}' AS snap";
        attachCmd.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in ImportTables)
            {
                using var checkCmd = conn.CreateCommand();
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT COUNT(*) FROM snap.sqlite_master WHERE type='table' AND name=@t";
                var p = checkCmd.CreateParameter();
                p.ParameterName = "t";
                p.Value = table;
                checkCmd.Parameters.Add(p);
                var exists = Convert.ToInt64(checkCmd.ExecuteScalar());
                if (exists == 0) continue;

                try
                {
                    using var importCmd = conn.CreateCommand();
                    importCmd.Transaction = tx;
                    importCmd.CommandText = $"INSERT OR IGNORE INTO [{table}] SELECT * FROM snap.[{table}]";
                    importCmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to import table [{table}] from snapshot: {ex.Message}", ex);
                }
            }

            using (var fkCheckCmd = conn.CreateCommand())
            {
                fkCheckCmd.Transaction = tx;
                fkCheckCmd.CommandText = "PRAGMA foreign_key_check";
                using var reader = fkCheckCmd.ExecuteReader();
                if (reader.Read())
                {
                    var violations = new List<string>();
                    do
                    {
                        violations.Add($"{reader.GetString(0)} rowid={reader.GetValue(1)} refs {reader.GetString(2)}");
                    } while (reader.Read() && violations.Count < 10);
                    throw new InvalidOperationException(
                        $"Foreign key violations after snapshot import: {string.Join(", ", violations)}");
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            using (var detachCmd = conn.CreateCommand())
            {
                detachCmd.CommandText = "DETACH DATABASE snap";
                detachCmd.ExecuteNonQuery();
            }
            using var fkOn = conn.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys = ON";
            fkOn.ExecuteNonQuery();
        }
    }

    private static async Task<byte[]> ExtractManifestFromTarGzAsync(string tarGzPath, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name == ManifestFileName)
            {
                await using var stream = entry.DataStream!;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
        }

        throw new InvalidOperationException("Snapshot does not contain manifest.json");
    }

    private static async Task ExtractTarGzAsync(string archivePath, string destDir, CancellationToken ct = default)
    {
        var compressedSize = new FileInfo(archivePath).Length;
        var maxTotalSize = Math.Min(compressedSize * 20, 50_000_000_000);
        const long maxFileCount = 1_000_000;
        long totalExtracted = 0;
        long fileCount = 0;

        await using var fs = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
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

    private static async Task VerifyManifestFileHashesAsync(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Snapshot manifest.json not found");

        var manifestText = await File.ReadAllTextAsync(dir + Path.DirectorySeparatorChar + ManifestFileName);
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

    // Domain separation tag for sync-export sidecar signature.  Must stay byte-for-byte
    // identical to SnapshotService.DomainTagSidecar on the server side. If you change
    // either, change both, or sig verification fails.
    private static readonly byte[] DomainTagSidecar = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    private static async Task<byte[]> ComputeSignaturePayloadAsync(byte[] manifestBytes, string tarGzPath, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(DomainTagSidecar);
        hasher.AppendData(manifestBytes);
        await using var fs = File.OpenRead(tarGzPath);
        var buffer = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return hasher.GetHashAndReset();
    }

    private void CleanupOrphanMediaFiles()
    {
        var registeredIds = new HashSet<Guid>();
        using (var conn = (SqliteConnection)_connFactory.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM tbl_media";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                registeredIds.Add(reader.GetGuid(0));
        }

        var mediaDir = Path.Combine(_dataDir, "media");
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
            _logger.LogInformation("Deleted {Count} orphan media files after import", orphansDeleted);
    }

    private sealed record ChallengeResponseDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}

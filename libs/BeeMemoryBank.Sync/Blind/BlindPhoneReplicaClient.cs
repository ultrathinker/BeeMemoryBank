using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Downloads and installs a phone replica. The partial archive and its authenticated metadata stay
/// in the caller-owned work directory, so a stopped Android worker resumes with a byte range rather
/// than accepting a new package in the middle of an old one.
/// </summary>
public sealed class BlindPhoneReplicaClient(
    DbConnectionFactory liveFactory,
    INodeIdentityRepository identityRepo,
    INodeAuthSigner signer,
    string dataDirectory,
    string databasePath,
    ILogger<BlindPhoneReplicaClient> logger,
    Func<string, string, CancellationToken, Task>? copyMediaFile = null,
    TimeProvider? time = null)
{
    private const string InstallFailureFileName = "replica.install-failed.json";
    private const string BackupFailureFileName = "backup-package.failed.json";
    private const string BackupPartName = "backup-package.part";
    private const string BackupArchiveName = "backup-package.tar.gz";
    private const int MaxAnchorIds = 200;
    // After a complete archive failed to verify or install, the next download waits 1 h, then 2 h, 4 h
    // ... up to a day: the same package would fail the same way, and each try costs the whole archive.
    private static readonly TimeSpan InstallBackOffBase = TimeSpan.FromHours(1);
    private static readonly TimeSpan InstallBackOffCap = TimeSpan.FromHours(24);
    private const string PackageHashHeader = "X-BMB-Package-Sha256";
    private const string SignatureHeader = "X-BMB-Snapshot-Signature";
    private const long MaximumManifestBytes = 1L * 1024 * 1024;
    private const long MaximumExtractedEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    private static readonly byte[] SignatureDomain = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task FetchAndInstallAsync(
        HttpClient http, BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        var identity = await identityRepo.GetAsync()
            ?? throw new InvalidOperationException("The blind phone identity has not been recorded.");
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException("The blind phone requires its v=2 external-key identity before replica download.");

        Directory.CreateDirectory(workDirectory);
        CleanupExtractionArtifacts(workDirectory);
        await ThrowIfBackingOffAsync(workDirectory, target.NodeId, ct);
        var token = await PeerAuthenticator.AuthenticateAsync(signer, http, target.Address, identity, target.NodeId, ct);
        var partPath = Path.Combine(workDirectory, "replica.part");
        var metadataPath = Path.Combine(workDirectory, "replica.part.json");
        try
        {
            var current = await DownloadAsync(http, target, token, partPath, metadataPath, progress, ct);
            try
            {
                var extraction = Path.Combine(workDirectory, "verified-" + current.Sha256.ToLowerInvariant());
                await VerifyAndExtractAsync(partPath, extraction, current, target, ct);
                await BuildAndSwitchDatabaseAsync(extraction, target, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The archive is complete, and a complete partial is never resumed (the next attempt
                // starts over), so keeping it only holds the disk. The same package would fail the
                // same way: drop it, remember the failure and wait before downloading again.
                CleanupDownloadArtifacts(workDirectory, partPath, metadataPath);
                await RecordInstallFailureAsync(workDirectory, target.NodeId, ex);
                if (ex is InvalidDataException or IOException) throw;
                throw new InvalidDataException(
                    $"The downloaded replica could not be installed ({ex.GetType().Name}: {ex.Message})", ex);
            }
            CleanupDownloadArtifacts(workDirectory, partPath, metadataPath);
            ClearInstallFailure(workDirectory);
            progress?.Report(1);
            logger.LogInformation("Installed verified blind replica {Hash} from {NodeId}", current.Sha256, target.NodeId);
        }
        catch (InvalidDataException)
        {
            // A complete archive that did not verify must not turn the next attempt into an invalid
            // range request. Network and cancellation errors deliberately retain their partial bytes.
            CleanupDownloadArtifacts(workDirectory, partPath, metadataPath);
            throw;
        }
        finally
        {
            // Partial response bytes remain resumable after transport/cancellation failures, but an
            // extracted staging tree is never resumable or safe to reuse after any failed install.
            CleanupExtractionArtifacts(workDirectory);
        }
    }

    /// <summary>
    /// Downloads the listener's current package and verifies it exactly as an install does, but installs
    /// nothing: the bytes and the detached signature are handed back for a backup body. A backup needs a
    /// fresh package, not the first load's: a restore looks for the signed events of the package's state
    /// anchors, and a phone holds those events only for anchors that reached it after its own first load.
    /// It has its own partial (<c>backup-package.part</c>) and its own back-off record, so it neither resumes
    /// nor blocks the first load. The caller owns <see cref="VerifiedReplicaPackage.ArchivePath"/>.
    /// </summary>
    /// <param name="beforeDownload">Told the package size once the response headers are in and before the
    /// first byte is written; throwing stops the download with the partial left as it was.</param>
    public async Task<VerifiedReplicaPackage> FetchVerifiedPackageAsync(
        HttpClient http, BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct,
        Action<long>? beforeDownload = null)
    {
        var identity = await identityRepo.GetAsync()
            ?? throw new InvalidOperationException("The blind phone identity has not been recorded.");
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException("The blind phone requires its v=2 external-key identity before a package download.");

        Directory.CreateDirectory(workDirectory);
        CleanupBackupExtractions(workDirectory);
        await ThrowIfBackingOffAsync(workDirectory, target.NodeId, ct, BackupFailureFileName);
        var token = await PeerAuthenticator.AuthenticateAsync(signer, http, target.Address, identity, target.NodeId, ct);
        var partPath = Path.Combine(workDirectory, BackupPartName);
        var metadataPath = partPath + ".json";
        PackageMetadata current;
        try
        {
            current = await DownloadAsync(http, target, token, partPath, metadataPath, progress, ct, beforeDownload);
        }
        catch (InvalidDataException ex)
        {
            // The whole archive came and did not match its signed headers: it is not resumable, and the same
            // package would fail the same way.
            ResetPartial(partPath, metadataPath);
            await RecordInstallFailureAsync(workDirectory, target.NodeId, ex, BackupFailureFileName);
            throw;
        }

        try
        {
            var extraction = Path.Combine(workDirectory, "backup-verified-" + current.Sha256.ToLowerInvariant());
            await VerifyAndExtractAsync(partPath, extraction, current, target, ct);
            var anchors = await ReadAnchorIdsAsync(extraction, ct);
            var boxes = await CountRecoveryBoxesAsync(extraction, ct);
            var archivePath = Path.Combine(workDirectory, BackupArchiveName);
            File.Move(partPath, archivePath, overwrite: true);
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            ClearInstallFailure(workDirectory, BackupFailureFileName);
            progress?.Report(1);
            return new VerifiedReplicaPackage(archivePath, Convert.FromBase64String(current.SignatureB64),
                current.Sha256.ToLowerInvariant(), current.Length, anchors, boxes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A complete partial is never resumed: drop it, remember the failure and wait before the next try.
            ResetPartial(partPath, metadataPath);
            await RecordInstallFailureAsync(workDirectory, target.NodeId, ex, BackupFailureFileName);
            if (ex is InvalidDataException or IOException) throw;
            throw new InvalidDataException(
                $"The downloaded package could not be verified ({ex.GetType().Name}: {ex.Message})", ex);
        }
        finally
        {
            CleanupBackupExtractions(workDirectory);
        }
    }

    private static void CleanupBackupExtractions(string workDirectory)
    {
        foreach (var directory in Directory.GetDirectories(workDirectory, "backup-verified-*"))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>The active recovery boxes of the extracted package's database; none when it has no such table.</summary>
    private static async Task<int> CountRecoveryBoxesAsync(string extraction, CancellationToken ct)
    {
        var db = Path.Combine(extraction, "beememorybank.db");
        if (!File.Exists(db)) return 0;
        await using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_recovery_box'") == 0)
            return 0;
        return (int)await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_recovery_box WHERE status = 'A'");
    }

    /// <summary>The newest anchors of the extracted package (newest first); none when it has no such table.</summary>
    private static async Task<IReadOnlyList<string>> ReadAnchorIdsAsync(string extraction, CancellationToken ct)
    {
        var db = Path.Combine(extraction, "beememorybank.db");
        if (!File.Exists(db)) return [];
        await using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_state_anchor'") == 0)
            return [];
        return (await conn.QueryAsync<string>(
            "SELECT anchor_id FROM tbl_state_anchor ORDER BY created_at DESC, lamport_ts DESC LIMIT @Max", new { Max = MaxAnchorIds })).ToList();
    }

    private async Task ThrowIfBackingOffAsync(string workDirectory, Guid nodeId, CancellationToken ct,
        string fileName = InstallFailureFileName)
    {
        var failure = await ReadInstallFailureAsync(Path.Combine(workDirectory, fileName), ct);
        if (failure is null || failure.NodeId != nodeId || failure.RetryAfter <= UtcNow()) return;
        throw new InvalidDataException(
            $"The last replica install failed {failure.Attempts} time(s) ({failure.Error}); next attempt after {failure.RetryAfter:u}.");
    }

    private async Task RecordInstallFailureAsync(string workDirectory, Guid nodeId, Exception cause,
        string fileName = InstallFailureFileName)
    {
        var path = Path.Combine(workDirectory, fileName);
        try
        {
            var prior = await ReadInstallFailureAsync(path, CancellationToken.None);
            var attempts = prior is not null && prior.NodeId == nodeId ? prior.Attempts + 1 : 1;
            var delay = TimeSpan.FromTicks(Math.Min(InstallBackOffCap.Ticks, InstallBackOffBase.Ticks << Math.Min(attempts - 1, 5)));
            var record = new InstallFailure(nodeId, attempts, UtcNow() + delay, cause.GetType().Name);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, Json));
            logger.LogWarning(cause, "Blind replica install failed (attempt {Attempts}); next download after {RetryAfter:u}",
                attempts, record.RetryAfter);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not recording only costs the back-off; it must not hide the failure being reported.
            logger.LogWarning(ex, "Could not record the blind replica install failure");
        }
    }

    private static void ClearInstallFailure(string workDirectory, string fileName = InstallFailureFileName)
    {
        var path = Path.Combine(workDirectory, fileName);
        if (File.Exists(path)) File.Delete(path);
    }

    private static async Task<InstallFailure?> ReadInstallFailureAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallFailure>(await File.ReadAllTextAsync(path, ct), Json);
        }
        catch (JsonException)
        {
            return null; // An unreadable record is as good as none: the back-off only ever delays.
        }
    }

    private DateTimeOffset UtcNow() => (time ?? TimeProvider.System).GetUtcNow();

    private static async Task<PackageMetadata> DownloadAsync(
        HttpClient http, BlindCallCode target, string token, string partPath, string metadataPath,
        IProgress<double>? progress, CancellationToken ct, Action<long>? beforeDownload = null)
    {
        // A restart is only needed when authenticated resume state cannot describe the response.
        // A transport failure remains resumable: it is intentionally not caught here.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var partExists = File.Exists(partPath);
            var offset = partExists ? new FileInfo(partPath).Length : 0;
            PackageMetadata? prior;
            try
            {
                prior = await ReadMetadataAsync(metadataPath, ct);
            }
            catch (InvalidDataException)
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }

            if ((partExists && offset == 0) ||
                (offset > 0 && (prior is null || offset >= prior.Length)))
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.Address.TrimEnd('/')}/api/blind/replica");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }
            response.EnsureSuccessStatusCode();

            PackageMetadata current;
            try
            {
                current = ReadMetadata(response, offset);
            }
            catch (InvalidDataException) when (offset > 0)
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }

            if ((prior is not null && !prior.Matches(current)) ||
                (offset > 0 && response.Content.Headers.ContentRange?.From != offset))
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }
            beforeDownload?.Invoke(current.Length);
            await WriteMetadataAsync(metadataPath, current, ct);

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partPath, offset == 0 ? FileMode.Create : FileMode.Open,
                             FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous))
            {
                if (offset > 0) output.Position = offset;
                var buffer = new byte[81_920];
                var copied = offset;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    copied += count;
                    progress?.Report(Math.Min(1, (double)copied / current.Length));
                }
                await output.FlushAsync(ct);
            }

            if (new FileInfo(partPath).Length != current.Length)
                throw new InvalidDataException("The replica download ended before its signed content length.");
            var hash = await HashFileAsync(partPath, ct);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(current.Sha256)))
                throw new InvalidDataException("The replica package SHA-256 does not match its signed response header.");
            return current;
        }

        throw new InvalidDataException("The replica could not be resumed from its authenticated partial state.");
    }

    private static PackageMetadata ReadMetadata(HttpResponseMessage response, long offset)
    {
        var sha = ReadSingleHeader(response, PackageHashHeader);
        var signature = ReadSingleHeader(response, SignatureHeader);
        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        if (length is not > 0 || (offset > 0 && length <= offset))
            throw new InvalidDataException("Replica response has no valid complete content length.");
        ValidatePartialMetadata(sha, signature, length.Value);
        return new PackageMetadata(sha!.ToUpperInvariant(), signature!, length.Value);
    }

    private static string? ReadSingleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var firstTwo = values.Take(2).ToArray();
        return firstTwo.Length == 1 ? firstTwo[0] : null;
    }

    private static async Task<PackageMetadata?> ReadMetadataAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        PackageMetadata? metadata;
        try { metadata = await JsonSerializer.DeserializeAsync<PackageMetadata>(stream, Json, ct); }
        catch (JsonException ex) { throw new InvalidDataException("Partial replica metadata is invalid.", ex); }
        if (metadata is null)
            throw new InvalidDataException("Partial replica metadata is empty.");
        ValidatePartialMetadata(metadata.Sha256, metadata.SignatureB64, metadata.Length);
        return metadata;
    }

    private static async Task WriteMetadataAsync(string path, PackageMetadata value, CancellationToken ct)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             4_096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Rejects persisted resume fields that could otherwise throw outside the reset path.</summary>
    internal static void ValidatePartialMetadata(string? sha256, string? signatureB64, long length)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"Replica response has no valid {PackageHashHeader} header.");
        if (signatureB64 is null)
            throw new InvalidDataException($"Replica response has no {SignatureHeader} header.");
        try
        {
            if (Convert.FromBase64String(signatureB64).Length != 64)
                throw new InvalidDataException("Replica signature must be an Ed25519 signature.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Replica signature is not Base64.", ex);
        }
        if (length <= 0)
            throw new InvalidDataException("Replica response has no valid complete content length.");
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static void ResetPartial(string partPath, string metadataPath)
    {
        if (File.Exists(partPath)) File.Delete(partPath);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
    }

    private static void CleanupDownloadArtifacts(string workDirectory, string partPath, string metadataPath)
    {
        ResetPartial(partPath, metadataPath);
        CleanupExtractionArtifacts(workDirectory);
    }

    private static void CleanupExtractionArtifacts(string workDirectory)
    {
        foreach (var directory in Directory.GetDirectories(workDirectory, "verified-*"))
            Directory.Delete(directory, recursive: true);
    }

    private static async Task VerifyAndExtractAsync(
        string archivePath, string extraction, PackageMetadata metadata, BlindCallCode target, CancellationToken ct)
    {
        Directory.CreateDirectory(extraction);
        var manifest = await ReadEntryAsync(archivePath, "manifest.json", ct)
            ?? throw new InvalidDataException("Replica package has no manifest.json.");
        var signaturePayload = await SignaturePayloadAsync(manifest, archivePath, ct);
        if (!Ed25519Signer.Verify(target.PublicKey, signaturePayload, Convert.FromBase64String(metadata.SignatureB64)))
            throw new InvalidDataException("Replica detached signature does not verify under the call-code node key.");

        await ExtractAsync(archivePath, extraction, ct);
        await VerifySnapshotHashesAsync(extraction, ct);
        var blindManifestBytes = await File.ReadAllBytesAsync(Path.Combine(extraction, BlindManifest.FileName), ct);
        var blindManifest = BlindManifest.Parse(blindManifestBytes);
        if (blindManifest.ProducerNodeId != target.NodeId)
            throw new InvalidDataException("Replica package producer does not match the call-code node.");
        var producer = blindManifest.Whitelist.SingleOrDefault(p => p.NodeId == target.NodeId)
            ?? throw new InvalidDataException("Replica package does not whitelist its producer.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(producer.PublicKeyB64), target.PublicKey))
            throw new InvalidDataException("Replica package producer key does not match the call-code key.");
    }

    private async Task BuildAndSwitchDatabaseAsync(string extraction, BlindCallCode target, CancellationToken ct)
    {
        var sourceDb = Path.Combine(extraction, "beememorybank.db");
        if (!File.Exists(sourceDb)) throw new InvalidDataException("Replica package does not contain a database.");
        var blindManifest = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(extraction, BlindManifest.FileName), ct));
        var candidatePath = Path.Combine(Path.GetDirectoryName(databasePath)!,
            $"beememorybank.replica-{blindManifest.SeedId:N}.db");

        var self = await identityRepo.GetAsync() ?? throw new InvalidOperationException("Blind phone identity disappeared during install.");
        DeleteCandidateIfPresent(candidatePath);
        try
        {
            using (var candidateFactory = new DbConnectionFactory(candidatePath))
            {
                await new MigrationRunner(candidateFactory).RunMigrationsAsync();
                await new NodeIdentityRepository(candidateFactory).CreateAsync(self);
                ImportReplicaTables(candidateFactory, sourceDb);
                var whitelist = new WhitelistRepository(candidateFactory);
                foreach (var peer in blindManifest.Whitelist.Where(p => p.NodeId != self.NodeId))
                {
                    await whitelist.CreateAsync(new WhitelistEntry
                    {
                        NodeId = peer.NodeId,
                        DisplayName = peer.DisplayName,
                        Ed25519PublicKey = Convert.FromBase64String(peer.PublicKeyB64),
                        ApiAddress = peer.ApiAddress,
                        IsSuperadmin = peer.IsSuperadmin,
                        TlsSpki = peer.TlsSpki,
                        Status = WhitelistStatuses.Active,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        LamportTs = peer.LamportTs,
                        SourceNodeId = peer.SourceNodeId
                    });
                }
                await new SyncPositionRepository(candidateFactory).UpsertAsync(new SyncPosition
                {
                    RemoteNodeId = target.NodeId,
                    LastSequenceNum = blindManifest.CpSequence,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            using var writeGate = await EventWriteGate.Instance.QuiesceAsync(ct);
            using var ownerFlow = EventWriteGate.EnterOwnerFlow();
            using var quiesced = liveFactory.BeginQuiesce();
            await liveFactory.WaitDrainedAsync(TimeSpan.FromSeconds(30), ct);

            // A media copy failure must leave the old database live. Encrypted blobs are content-addressed,
            // so copying the new blobs before the database switch cannot invalidate the old replica.
            var mediaSource = Path.Combine(extraction, "media");
            var mediaTarget = Path.Combine(dataDirectory, "media");
            if (Directory.Exists(mediaSource))
            {
                Directory.CreateDirectory(mediaTarget);
                foreach (var file in Directory.GetFiles(mediaSource, "*.enc"))
                    await (copyMediaFile ?? CopyMediaFileAsync)(file,
                        Path.Combine(mediaTarget, Path.GetFileName(file)), ct);
            }

            if (File.Exists(databasePath))
            {
                var backup = databasePath + $".before-replica-{blindManifest.SeedId:N}";
                File.Replace(candidatePath, databasePath, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(candidatePath, databasePath);
            }
            PruneReplicaRollbackDatabases();
        }
        finally
        {
            DeleteCandidateIfPresent(candidatePath);
        }
    }

    private static void DeleteCandidateIfPresent(string candidatePath)
    {
        if (!File.Exists(candidatePath)) return;
        // A failed migration/import has disposed its connections, but Microsoft.Data.Sqlite may still
        // retain one in this candidate database's pool. Clear only that path's pool before replacing it.
        using var candidate = new SqliteConnection($"Data Source={candidatePath}");
        SqliteConnection.ClearPool(candidate);
        File.Delete(candidatePath);
    }

    private static Task CopyMediaFileAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Copy(source, destination, overwrite: true);
        return Task.CompletedTask;
    }

    private void PruneReplicaRollbackDatabases()
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory)) return;
        var pattern = Path.GetFileName(databasePath) + ".before-replica-*";
        foreach (var obsolete in Directory.GetFiles(directory, pattern)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(1))
        {
            try
            {
                File.Delete(obsolete);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not remove obsolete blind replica rollback database {Path}", obsolete);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Could not remove obsolete blind replica rollback database {Path}", obsolete);
            }
        }
    }

    private static void ImportReplicaTables(DbConnectionFactory factory, string sourceDb)
    {
        using var conn = (SqliteConnection)factory.CreateConnection();
        conn.Execute("PRAGMA foreign_keys = OFF");
        conn.Execute($"ATTACH DATABASE '{sourceDb.Replace("'", "''")}' AS snap");
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in SnapshotTables.Replicated.Concat(["tbl_hard_delete_audit"]))
                if (SnapshotTableImport.SnapshotHasTable(conn, tx, table))
                    SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
            SnapshotTableImport.AdoptLegacyInlineCiphertext(conn, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            conn.Execute("DETACH DATABASE snap");
            conn.Execute("PRAGMA foreign_keys = ON");
        }
    }

    private static async Task<byte[]?> ReadEntryAsync(string archive, string name, CancellationToken ct)
    {
        await using var file = File.OpenRead(archive);
        return await ReadArchiveEntryAsync(file, name, MaximumManifestBytes, ct);
    }

    internal static async Task<byte[]?> ReadArchiveEntryAsync(
        Stream archive, string name, long maximumBytes, CancellationToken ct)
    {
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gzip, leaveOpen: true);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name != name || entry.DataStream is null) continue;
            await using var input = entry.DataStream;
            using var output = new MemoryStream();
            await CopyWithLimitAsync(input, output, maximumBytes, ct);
            return output.ToArray();
        }
        return null;
    }

    internal static async Task ExtractAsync(
        string archive, string destination, CancellationToken ct,
        long maximumEntryBytes = MaximumExtractedEntryBytes, long maximumTotalBytes = MaximumExtractedBytes)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        long extractedBytes = 0;
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;
            var outputPath = Path.GetFullPath(Path.Combine(destination, entry.Name));
            if (!outputPath.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"Replica archive path escapes its staging directory: {entry.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81_920, FileOptions.Asynchronous);
            var remaining = maximumTotalBytes - extractedBytes;
            if (remaining <= 0)
                throw new InvalidDataException("Replica archive exceeds the maximum extracted size.");
            var copied = await CopyWithLimitAsync(entry.DataStream, output,
                Math.Min(maximumEntryBytes, remaining), ct);
            extractedBytes += copied;
        }
    }

    /// <summary>Copies at most <paramref name="maximumBytes"/> and rejects a stream with further bytes.</summary>
    internal static async Task<long> CopyWithLimitAsync(
        Stream input, Stream output, long maximumBytes, CancellationToken ct)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var buffer = new byte[81_920];
        long copied = 0;
        while (true)
        {
            var remaining = maximumBytes - copied;
            if (remaining == 0)
            {
                if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
                    throw new InvalidDataException("Replica archive entry exceeds its allowed size.");
                return copied;
            }

            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (count == 0) return copied;
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            copied += count;
        }
    }

    private static async Task VerifySnapshotHashesAsync(string directory, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Name));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path))
                throw new InvalidDataException($"Replica manifest file is missing: {file.Name}");
            var actual = Convert.ToHexStringLower(await HashFileAsync(path, ct));
            if (!string.Equals(actual, file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Replica manifest hash mismatch: {file.Name}");
        }
    }

    private static async Task<byte[]> SignaturePayloadAsync(byte[] manifest, string archive, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SignatureDomain);
        hash.AppendData(manifest);
        await using var stream = File.OpenRead(archive);
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }

    private sealed record InstallFailure(Guid NodeId, int Attempts, DateTimeOffset RetryAfter, string Error);

    private sealed record PackageMetadata(string Sha256, string SignatureB64, long Length)
    {
        public bool Matches(PackageMetadata other) =>
            Length == other.Length &&
            string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase) &&
            CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(SignatureB64), Convert.FromBase64String(other.SignatureB64));
    }
}

/// <summary>
/// A replica package downloaded and verified like an install does it (signature under the call-code key,
/// every file against the signed manifest, producer and key against the call code) but NOT installed.
/// </summary>
/// <param name="ArchivePath">The archive exactly as the listener served it; its detached signature covers these bytes.</param>
/// <param name="Signature">The detached Ed25519 signature the listener sent with it.</param>
/// <param name="AnchorIds">The package's newest state anchors (newest first): the rows a restore will look for signed events of.</param>
/// <param name="RecoveryBoxes">How many active recovery boxes the package's database holds: a restore takes its keys from them.</param>
public sealed record VerifiedReplicaPackage(
    string ArchivePath, byte[] Signature, string Sha256, long Length, IReadOnlyList<string> AnchorIds, int RecoveryBoxes = 1);

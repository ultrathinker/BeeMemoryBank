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

public class SnapshotService
{
    private const string DbFileName = "beememorybank.db";
    private const string ManifestFileName = "manifest.json";

    private static readonly string[] SecretTables =
    [
        "tbl_node_identity", "tbl_session", "tbl_agent", "tbl_agent_access",
        "tbl_sync_position", "tbl_sync_push_position", "tbl_compaction_log", "tbl_event",
        "tbl_key_slot", "tbl_user", "tbl_folder_acl_entry", "tbl_audit_log",
        "tbl_hard_delete_audit"
        // NOTE: tbl_projection_matrix is intentionally INCLUDED in the snapshot:
        // the matrix is shared across nodes with the same master DEK so that
        // concept-tag embeddings remain compatible. Each node's DEK wraps the
        // same plaintext matrix on all peers.
    ];

    private const string DbEncryptionMagicV1 = "BMBDB1";
    private const string DbEncryptionMagicV2 = "BMBDB2";
    private const int DbEncryptionOverheadV1 = 6 + 12 + 16;
    private const int DbEncryptionOverheadV2 = 6 + 16 + 12 + 16;
    private const long MaxEncryptableDbSize = 2L * 1024 * 1024 * 1024;

    private readonly string _dataPath;
    private readonly DbConnectionFactory _connFactory;
    private readonly INodeIdentityRepository? _nodeRepo;
    private readonly ILamportClock? _clock;
    private readonly ILogger<SnapshotService>? _logger;
    private readonly IRestoreReplayShieldRepository? _replayShieldRepo;
    private readonly IWhitelistRepository? _whitelistRepo;
    private readonly BeeMemoryBank.Core.Services.SessionService? _sessionService;

    public string SnapshotsDir => Path.Combine(_dataPath, "snapshots");

    public SnapshotService(string dataPath, DbConnectionFactory connFactory,
        INodeIdentityRepository? nodeRepo = null, ILamportClock? clock = null,
        ILogger<SnapshotService>? logger = null,
        IRestoreReplayShieldRepository? replayShieldRepo = null,
        IWhitelistRepository? whitelistRepo = null,
        BeeMemoryBank.Core.Services.SessionService? sessionService = null)
    {
        _dataPath = dataPath;
        _connFactory = connFactory;
        _nodeRepo = nodeRepo;
        _clock = clock;
        _logger = logger;
        _replayShieldRepo = replayShieldRepo;
        _whitelistRepo = whitelistRepo;
        _sessionService = sessionService;
    }

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

    /// <summary>
    /// Create a snapshot of the current node's database (and media).
    /// </summary>
    /// <param name="filterSecrets">Strip identity/keys/secrets — for distribution to peers. Local backups keep them.</param>
    /// <param name="sign">Embed Ed25519 signature of (manifest || file) by this node. Default true:
    /// every snapshot is signed by its creator so it can later participate in network-wide restore
    /// without manual re-signing. The signature is provenance proof, never a secret.</param>
    /// <param name="cpSequenceNum">Lamport checkpoint sequence number — set by compaction/sync paths.</param>
    public async Task<SnapshotInfo> CreateAsync(
        bool filterSecrets = true,
        bool sign = true,
        long? cpSequenceNum = null,
        bool encryptDb = true)
    {
        Directory.CreateDirectory(SnapshotsDir);

        try
        {
            var dbPath = Path.Combine(_dataPath, DbFileName);
            if (File.Exists(dbPath))
            {
                var dbSize = new FileInfo(dbPath).Length;
                var tempDriveInfo = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
                var snapshotsDriveInfo = new DriveInfo(Path.GetPathRoot(SnapshotsDir)!);
                var requiredBytes = dbSize * 2;
                if (tempDriveInfo.AvailableFreeSpace < requiredBytes)
                    throw new InvalidOperationException(
                        $"Insufficient disk space for snapshot: need ~{requiredBytes / (1024 * 1024)}MB in {tempDriveInfo.Name}, have {tempDriveInfo.AvailableFreeSpace / (1024 * 1024)}MB");
                if (snapshotsDriveInfo.AvailableFreeSpace < requiredBytes)
                    throw new InvalidOperationException(
                        $"Insufficient disk space for snapshot: need ~{requiredBytes / (1024 * 1024)}MB in {snapshotsDriveInfo.Name}, have {snapshotsDriveInfo.AvailableFreeSpace / (1024 * 1024)}MB");
            }
        }
        catch (ArgumentException) { /* DriveInfo can fail on unusual paths */ }
        catch (DriveNotFoundException) { }

        var tempDb = Path.GetTempFileName();
        try
        {
            using (var conn = (SqliteConnection)_connFactory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{tempDb}'";
                cmd.ExecuteNonQuery();
            }

            if (filterSecrets)
                FilterSecretsFrom(tempDb);

            bool dbEncrypted = false;
            if (encryptDb && _sessionService is { IsUnlocked: true })
            {
                var masterDek = _sessionService.GetMasterDek();
                try
                {
                    await EncryptDbFileAsync(tempDb, masterDek);
                    dbEncrypted = true;
                }
                finally
                {
                    Array.Clear(masterDek);
                }
            }

            var allFiles = new Dictionary<string, string>();

            var dbHash = await ComputeHashAsync(tempDb);
            allFiles[DbFileName] = dbHash;

            var mediaDir = Path.Combine(_dataPath, "media");
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

            long? lamportTsAtCp = null;
            Guid? producerNodeId = null;
            int? migrationVersion = null;

            if (cpSequenceNum != null)
            {
                lamportTsAtCp = _clock?.Current;
                if (lamportTsAtCp == null)
                {
                    using var lcConn = _connFactory.CreateConnection();
                    using var lcCmd = lcConn.CreateCommand();
                    lcCmd.CommandText = "SELECT MAX(lamport_ts) FROM tbl_event";
                    var lcResult = lcCmd.ExecuteScalar();
                    if (lcResult != null && lcResult != DBNull.Value)
                        lamportTsAtCp = Convert.ToInt64(lcResult);
                }
            }

            // Always include producerNodeId when signing — upload/network-restore flow needs
            // it to look up originator's pubkey in whitelist for verification. This is provenance
            // info, not a secret.
            if (sign && _nodeRepo != null)
            {
                var nodeIdentity = await _nodeRepo.GetAsync();
                producerNodeId = nodeIdentity?.NodeId;
            }
            else if (cpSequenceNum != null)
            {
                var nodeIdentity = _nodeRepo != null ? await _nodeRepo.GetAsync() : null;
                producerNodeId = nodeIdentity?.NodeId;
            }

            if (cpSequenceNum != null)
            {
                using var metaConn = _connFactory.CreateConnection();
                using var metaCmd = metaConn.CreateCommand();
                metaCmd.CommandText = "SELECT MAX(version) FROM tbl_migration";
                var result = metaCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    migrationVersion = Convert.ToInt32(result);
            }

            var manifestDict = new Dictionary<string, object?>
            {
                ["version"] = cpSequenceNum != null ? 3 : (mediaFiles.Count > 0 ? 2 : 1),
                ["createdAt"] = DateTime.UtcNow.ToString("o"),
                ["files"] = allFiles,
                ["snapshotFormatVersion"] = 4,
                ["dbEncrypted"] = dbEncrypted
            };

            // Producer node id always included when known. Upload-side signature verification
            // (SaveUploadedAsync) requires this field to find the originator in whitelist.
            if (producerNodeId != null)
                manifestDict["producerNodeId"] = producerNodeId.ToString();

            if (cpSequenceNum != null)
            {
                manifestDict["cpSequenceNum"] = cpSequenceNum.Value;
                manifestDict["lamportTsAtCp"] = lamportTsAtCp;
                manifestDict["migrationVersion"] = migrationVersion;
            }

            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifestDict, jsonOpts));

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"bmb-snapshot-{timestamp}.tar.gz";
            var filePath = Path.Combine(SnapshotsDir, fileName);

            // Two complementary signatures over the manifest exist for historical reasons:
            //   1) Sidecar `{filePath}.sig` over (manifest || file-content) — used by the
            //      sync-export RestoreForJoinAsync verify path. Required for backward compat.
            //   2) Embedded `manifest.json.sig` over (manifest only) — read by the upload
            //      verify path (SaveUploadedAsync). Lets a single tar.gz be self-contained
            //      for distribution / network-wide restore without juggling sidecars.
            // File integrity for (2) flows transitively: manifest contains SHA256 of every
            // file inside, manifest is signed, so a file tampering changes its hash → manifest
            // either no longer matches actual file (verifier checks) or signature fails.
            byte[]? manifestSignature = null;
            if (sign)
            {
                if (_nodeRepo == null)
                    throw new InvalidOperationException("Node identity repository not available for signing");
                var nodeIdentity = await _nodeRepo.GetAsync()
                    ?? throw new InvalidOperationException("Node identity not found");
                // Domain separation: prepend a fixed tag so this signature can NEVER be
                // confused with the sidecar signature (which uses tag MANIFEST-FILE-V1
                // and signs manifest||file content). Without separation, a future verifier
                // bug could feed an embedded sig to the sidecar verify path or vice versa
                // and silently fail-open. Different tags make cross-format substitution
                // impossible by construction.
                var manifestPayload = BuildSigPayloadEmbedded(manifestBytes);
                manifestSignature = SignWithIdentityAuto(nodeIdentity, manifestPayload);
            }

            await using (var fs = File.Create(filePath))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
            {
                {
                    await using var dbStream = File.OpenRead(tempDb);
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName)
                    {
                        DataStream = dbStream
                    });
                }

                foreach (var encFile in mediaFiles)
                {
                    var relativePath = $"media/{Path.GetFileName(encFile)}";
                    await using var mediaStream = File.OpenRead(encFile);
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, relativePath)
                    {
                        DataStream = mediaStream
                    });
                }

                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName)
                {
                    DataStream = new MemoryStream(manifestBytes)
                });

                if (manifestSignature != null)
                {
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName + ".sig")
                    {
                        DataStream = new MemoryStream(manifestSignature)
                    });
                }
            }

            if (sign)
            {
                // Sidecar signature over (manifest || file content) for sync-export back-compat.
                var nodeIdentity = await _nodeRepo!.GetAsync()
                    ?? throw new InvalidOperationException("Node identity not found");
                var sidecarPayload = await ComputeSignaturePayloadAsync(manifestBytes, filePath);
                var sidecarSig = SignWithIdentityAuto(nodeIdentity, sidecarPayload);
                await File.WriteAllBytesAsync($"{filePath}.sig", sidecarSig);
            }

            var fi = new FileInfo(filePath);
            return new SnapshotInfo(fileName, fi.Length, fi.LastWriteTimeUtc,
                cpSequenceNum, producerNodeId, sign);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    public int PruneOldSnapshots(int keepCount = 2)
    {
        Directory.CreateDirectory(SnapshotsDir);
        var files = Directory.GetFiles(SnapshotsDir, "*.tar.gz")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (files.Count <= keepCount) return 0;

        int deleted = 0;
        foreach (var fi in files.Skip(keepCount))
        {
            File.Delete(fi.FullName);
            var sigPath = $"{fi.FullName}.sig";
            if (File.Exists(sigPath))
                File.Delete(sigPath);
            deleted++;
        }

        return deleted;
    }

    public bool Delete(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return false;

        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath)) return false;

        File.Delete(filePath);
        var sigPath = $"{filePath}.sig";
        if (File.Exists(sigPath))
            File.Delete(sigPath);

        return true;
    }

    public string GetSnapshotPath(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) && !safeName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid snapshot file name");
        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Snapshot {safeName} not found");
        return filePath;
    }

    public string? FindSnapshotFileById(Guid id)
    {
        Directory.CreateDirectory(SnapshotsDir);
        // Match the exact filename suffix `-<id:N>.<ext>` to avoid substring collisions on
        // arbitrary filenames in the directory. SaveUploadedAsync names files
        // `imported-<originator>-<id:N>.bin`; CreateAsync uses `bmb-snapshot-<timestamp>.tar.gz`
        // (timestamps never contain a 32-hex GUID by construction).
        var idStr = id.ToString("N");
        var rootedDir = Path.GetFullPath(SnapshotsDir);
        foreach (var file in Directory.GetFiles(SnapshotsDir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.EndsWith("-" + idStr, StringComparison.OrdinalIgnoreCase)) continue;
            // Defensive: ensure resolved path is inside SnapshotsDir.
            var resolved = Path.GetFullPath(file);
            if (!resolved.StartsWith(rootedDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && resolved != rootedDir) continue;
            return file;
        }
        return null;
    }

    public async Task<SnapshotUploadResponse> SaveUploadedAsync(Stream stream)
    {
        Directory.CreateDirectory(SnapshotsDir);
        var fileId = Guid.NewGuid();
        var tempFile = Path.Combine(SnapshotsDir, $".upload-{fileId:N}.tmp");
        var tempFileMoved = false;

        try
        {

        await using (var fs = File.Create(tempFile))
        {
            await stream.CopyToAsync(fs);
        }

        var hash = await ComputeHashAsync(tempFile);

        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-upload-{fileId:N}");
        string originatorNodeId = "unknown";
        string createdAt = DateTime.UtcNow.ToString("O");
        bool networkRestoreAllowed = true;
        string? dekMismatchReason = null;

        try
        {
            Directory.CreateDirectory(tempDir);

            // Pre-extract manifest and signature for verification BEFORE full extraction.
            // This prevents a malicious tar from extracting bombs before we validate provenance.
            byte[]? preManifestBytes = null;
            byte[]? preSigBytes = null;
            await using (var preFs = File.OpenRead(tempFile))
            await using (var preGz = new GZipStream(preFs, CompressionMode.Decompress))
            using (var preTar = new TarReader(preGz))
            {
                while (await preTar.GetNextEntryAsync() is { } entry)
                {
                    if (entry.EntryType != TarEntryType.RegularFile) continue;
                    if (entry.Name == ManifestFileName)
                    {
                        await using var ms = new MemoryStream();
                        await entry.DataStream!.CopyToAsync(ms);
                        preManifestBytes = ms.ToArray();
                    }
                    else if (entry.Name == ManifestFileName + ".sig")
                    {
                        await using var ms = new MemoryStream();
                        await entry.DataStream!.CopyToAsync(ms);
                        preSigBytes = ms.ToArray();
                    }
                }
            }

            if (preManifestBytes != null)
            {
                var manifest = JsonDocument.Parse(preManifestBytes);
                if (manifest.RootElement.TryGetProperty("producerNodeId", out var pid))
                    originatorNodeId = pid.GetString() ?? "unknown";
                if (manifest.RootElement.TryGetProperty("createdAt", out var ca))
                    createdAt = ca.GetString() ?? createdAt;

                if (originatorNodeId != "unknown")
                {
                    bool isSelf = false;
                    if (_nodeRepo != null)
                    {
                        var ident = await _nodeRepo.GetAsync();
                        if (ident != null && ident.NodeId.ToString() == originatorNodeId)
                            isSelf = true;
                    }

                    bool isTrusted = false;
                    BeeMemoryBank.Core.Models.WhitelistEntry? trustedEntry = null;
                    if (_whitelistRepo != null && Guid.TryParse(originatorNodeId, out var parsedOriginatorId))
                    {
                        trustedEntry = await _whitelistRepo.GetByNodeIdAsync(parsedOriginatorId);
                        if (trustedEntry != null) isTrusted = true;
                    }

                    if (!isSelf && !isTrusted)
                    {
                        networkRestoreAllowed = false;
                        dekMismatchReason = "Originator node not in whitelist (likely foreign network)";
                    }
                    else if (trustedEntry != null)
                    {
                        if (preSigBytes != null)
                        {
                            try
                            {
                                if (!Ed25519Signer.Verify(trustedEntry.Ed25519PublicKey, BuildSigPayloadEmbedded(preManifestBytes!), preSigBytes))
                                {
                                    // The archive *claims* to be from a peer we trust AND ships
                                    // a signature, but it doesn't verify against that peer's
                                    // public key. Either the file is tampered with or someone
                                    // forged the manifest's producerNodeId. Reject outright —
                                    // accepting it would let an attacker plant a fake snapshot
                                    // that the admin later restores in standalone mode (which
                                    // would adopt the claimed identity).
                                    _logger?.LogWarning("Rejected uploaded snapshot: manifest signature invalid for claimed originator {NodeId}.", originatorNodeId);
                                    throw new InvalidOperationException("Manifest signature is invalid for the claimed originator.");
                                }
                            }
                            catch (InvalidOperationException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Manifest signature read/verify error — rejecting upload.");
                                throw new InvalidOperationException("Manifest signature could not be read or verified.");
                            }
                        }
                        else
                        {
                            networkRestoreAllowed = false;
                            dekMismatchReason = "Snapshot is not signed (manifest.json.sig missing) — network-wide restore disabled";
                            _logger?.LogInformation("Uploaded snapshot from {NodeId} is unsigned; network-wide restore disabled.", originatorNodeId);
                        }
                    }
                }
                else
                {
                    networkRestoreAllowed = false;
                    dekMismatchReason = "Originator node not in whitelist (likely foreign network)";
                }
            }
            else
            {
                networkRestoreAllowed = false;
                dekMismatchReason = "Invalid snapshot format (missing manifest.json)";
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var shortOriginator = originatorNodeId == "unknown" ? "unknown" : (originatorNodeId.Length > 8 ? originatorNodeId[..8] : originatorNodeId);
        // Include the fileId at the end of the filename so FindSnapshotFileById can locate it via
        // exact suffix match (-<id:N>.bin) instead of fragile substring search across the dir.
        var fileName = $"imported-{shortOriginator}-{timestamp}-{fileId:N}.bin";
        var finalPath = Path.Combine(SnapshotsDir, fileName);
        
        File.Move(tempFile, finalPath, overwrite: true);
        tempFileMoved = true;
        var fi = new FileInfo(finalPath);

        return new SnapshotUploadResponse(
            FileId: fileId,
            FileName: fileName,
            FileSizeBytes: fi.Length,
            OriginatorNodeId: originatorNodeId,
            SnapshotHash: hash,
            CreatedAt: createdAt,
            NetworkRestoreAllowed: networkRestoreAllowed,
            DekMismatchReason: dekMismatchReason
        );
        }
        finally
        {
            // Defensive cleanup: if anything threw between File.Create(tempFile) and the final
            // File.Move (extract failure, manifest parse error, OOM during hash, etc), the
            // .upload-<guid>.tmp file would otherwise pile up in the snapshots dir indefinitely.
            if (!tempFileMoved && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* ignore */ }
            }
        }
    }

    public async Task RestoreAsync(string fileName, bool standaloneMode = false)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid snapshot file name");

        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Snapshot {safeName} not found");

        // Coordinate with CompactionService — both flows bulk-rewrite tbl_event.
        // Lock acquisition is the very first statement inside the try, so any failure
        // between WaitAsync and the try-block (theoretical: Path.Combine throwing on a
        // poisoned $TMPDIR) cannot leak the semaphore.
        await HeavyOperationLock.Instance.WaitAsync();
        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), $"bmb-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            await ExtractTarGzAsync(filePath, tempDir, new FileInfo(filePath).Length);

            await VerifyManifestAsync(tempDir);

            var dbPath = Path.Combine(_dataPath, "beememorybank.db");

            var extractedDb = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Snapshot does not contain a database file");

            await DecryptDbIfNeededAsync(extractedDb);

            if (standaloneMode)
            {
                // Atomic standalone restore: do everything (copy + identity regen + wipe) in a
                // staging file first, then atomic rename. If the process crashes anywhere before
                // the rename, the live DB is untouched and the staging file is cleaned up by
                // the startup recovery sweep (see Program.cs). This closes the identity-injection
                // window where an admin-triggered crash could leave us running with the snapshot
                // originator's Ed25519 private key.
                var stagingPath = dbPath + ".standalone-staging";
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
                File.Copy(extractedDb, stagingPath, overwrite: true);

                var newNodeId = Guid.NewGuid();
                var (pubKey, privKey) = Ed25519Signer.GenerateKeyPair();

                using (var stagingConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stagingPath.Replace("'", "''")}"))
                {
                    stagingConn.Open();
                    using var tx = stagingConn.BeginTransaction();
                    try
                    {
                        using var wipeNetCmd = stagingConn.CreateCommand();
                        wipeNetCmd.Transaction = tx;
                        wipeNetCmd.CommandText = @"
                            DELETE FROM tbl_whitelist;
                            DELETE FROM tbl_sync_position;
                            DELETE FROM tbl_sync_push_position;
                            DELETE FROM tbl_restore_replay_shield;
                            DELETE FROM tbl_event;";
                        wipeNetCmd.ExecuteNonQuery();

                        using var identityCmd = stagingConn.CreateCommand();
                        identityCmd.Transaction = tx;
                        identityCmd.CommandText = @"
                            UPDATE tbl_node_identity
                            SET node_id = @newNodeId,
                                ed25519_private_key = @newPrivKey,
                                ed25519_public_key = @newPubKey";

                        var p1 = identityCmd.CreateParameter();
                        p1.ParameterName = "newNodeId";
                        p1.Value = newNodeId.ToString();
                        identityCmd.Parameters.Add(p1);

                        var p2 = identityCmd.CreateParameter();
                        p2.ParameterName = "newPrivKey";
                        p2.Value = privKey;
                        identityCmd.Parameters.Add(p2);

                        var p3 = identityCmd.CreateParameter();
                        p3.ParameterName = "newPubKey";
                        p3.Value = pubKey;
                        identityCmd.Parameters.Add(p3);

                        var p4 = identityCmd.CreateParameter();
                        p4.ParameterName = "updatedAt";
                        p4.Value = DateTime.UtcNow.ToString("O");
                        identityCmd.Parameters.Add(p4);

                         identityCmd.ExecuteNonQuery();
                         tx.Commit();
                     }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }

                // Stage media files before DB swap
                var mediaStagingDirR = Path.Combine(_dataPath, "media.staging");
                if (Directory.Exists(mediaStagingDirR))
                {
                    try { Directory.Delete(mediaStagingDirR, true); } catch { }
                }
                Directory.CreateDirectory(mediaStagingDirR);
                var extractedMediaStaging = Path.Combine(tempDir, "media");
                if (Directory.Exists(extractedMediaStaging))
                {
                    foreach (var f in Directory.GetFiles(extractedMediaStaging, "*.enc"))
                        File.Copy(f, Path.Combine(mediaStagingDirR, Path.GetFileName(f)), overwrite: true);
                }

                // Now stagingPath has the fully-prepared DB (snapshot data + new local identity).
                // Atomically replace the live DB. ClearAllPools releases any pooled connections
                // so the OS isn't holding the file open during the move.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await Task.Delay(200);
                File.Move(stagingPath, dbPath, overwrite: true);

                // Atomic media swap after DB swap
                var mediaDirR = Path.Combine(_dataPath, "media");
                var mediaOldDirR = Path.Combine(_dataPath, "media.old");
                if (Directory.Exists(mediaStagingDirR) && Directory.GetFiles(mediaStagingDirR, "*.enc").Length > 0)
                {
                    if (Directory.Exists(mediaOldDirR))
                    {
                        try { Directory.Delete(mediaOldDirR, true); } catch { }
                    }
                    if (Directory.Exists(mediaDirR))
                    {
                        try { Directory.Move(mediaDirR, mediaOldDirR); } catch { }
                    }
                    Directory.Move(mediaStagingDirR, mediaDirR);
                    if (Directory.Exists(mediaOldDirR))
                    {
                        try { Directory.Delete(mediaOldDirR, true); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to sweep media.old/ after atomic swap"); }
                    }
                }
                else
                {
                    if (Directory.Exists(mediaStagingDirR))
                    {
                        try { Directory.Delete(mediaStagingDirR, true); } catch { }
                    }
                }

                _logger?.LogInformation("Standalone restore: identity regenerated. New node_id: {NodeId}. Network connections wiped. Event log sanitized.", newNodeId);
            }
            else
            {
                // Non-standalone (legacy) restore
                // Stage media files before DB swap
                var mediaStagingDirNs = Path.Combine(_dataPath, "media.staging");
                if (Directory.Exists(mediaStagingDirNs))
                {
                    try { Directory.Delete(mediaStagingDirNs, true); } catch { }
                }
                Directory.CreateDirectory(mediaStagingDirNs);
                var extractedMediaNs = Path.Combine(tempDir, "media");
                if (Directory.Exists(extractedMediaNs))
                {
                    foreach (var f in Directory.GetFiles(extractedMediaNs, "*.enc"))
                        File.Copy(f, Path.Combine(mediaStagingDirNs, Path.GetFileName(f)), overwrite: true);
                }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await Task.Delay(200);
                File.Copy(extractedDb, dbPath, overwrite: true);

                var mediaDirNs = Path.Combine(_dataPath, "media");
                var mediaOldDirNs = Path.Combine(_dataPath, "media.old");
                if (Directory.Exists(mediaStagingDirNs) && Directory.GetFiles(mediaStagingDirNs, "*.enc").Length > 0)
                {
                    if (Directory.Exists(mediaOldDirNs))
                    {
                        try { Directory.Delete(mediaOldDirNs, true); } catch { }
                    }
                    if (Directory.Exists(mediaDirNs))
                    {
                        try { Directory.Move(mediaDirNs, mediaOldDirNs); } catch { }
                    }
                    Directory.Move(mediaStagingDirNs, mediaDirNs);
                    if (Directory.Exists(mediaOldDirNs))
                    {
                        try { Directory.Delete(mediaOldDirNs, true); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to sweep media.old/"); }
                    }
                }
                else
                {
                    if (Directory.Exists(mediaStagingDirNs))
                    {
                        try { Directory.Delete(mediaStagingDirNs, true); } catch { }
                    }
                }

                using var conn = _connFactory.CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM tbl_sync_push_position";
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            HeavyOperationLock.Instance.Release();
        }
    }

    public async Task<(long CpSeq, long LamportTs)> RestoreForJoinAsync(
        string tarGzPath,
        byte[] signature,
        byte[] producerPublicKey)
    {
        var manifestBytes = await ExtractManifestFromTarGzAsync(tarGzPath);

        var payload = await ComputeSignaturePayloadAsync(manifestBytes, tarGzPath);

        if (!Ed25519Signer.Verify(producerPublicKey, payload, signature))
            throw new InvalidOperationException("Snapshot signature verification failed");

        var manifest = JsonDocument.Parse(manifestBytes);
        var version = manifest.RootElement.GetProperty("version").GetInt32();
        if (version < 3)
            throw new InvalidOperationException($"Snapshot manifest version {version} is too old; sync-export requires version 3+.");

        var snapMigrationVersion = manifest.RootElement.TryGetProperty("migrationVersion", out var mv) ? mv.GetInt32() : -1;
        using (var mvcConn = _connFactory.CreateConnection())
        using (var mvcCmd = mvcConn.CreateCommand())
        {
            mvcCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM tbl_migration";
            var localMigrationVersion = Convert.ToInt32(mvcCmd.ExecuteScalar());
            if (snapMigrationVersion > localMigrationVersion)
                throw new InvalidOperationException(
                    $"Snapshot was produced with schema version {snapMigrationVersion}, but this node is on version {localMigrationVersion}. Upgrade this node first.");
        }

        var cpSeq = manifest.RootElement.GetProperty("cpSequenceNum").GetInt64();
        var lamportTs = manifest.RootElement.GetProperty("lamportTsAtCp").GetInt64();

        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-join-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await ExtractTarGzAsync(tarGzPath, tempDir, new FileInfo(tarGzPath).Length);
            await VerifyManifestAsync(tempDir);

            var extractedDb = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Snapshot does not contain a database file");

            await DecryptDbIfNeededAsync(extractedDb);

            var importTables = new[]
            {
                "tbl_folder", "tbl_article", "tbl_article_body", "tbl_concept_tag",
                "tbl_article_concept_tag", "tbl_concept_tag_edge", "tbl_media",
                "tbl_tombstone", "tbl_conflict_version", "tbl_projection_matrix"
            };

            using (var conn = (SqliteConnection)_connFactory.CreateConnection())
            {
                using (var fkOff = conn.CreateCommand())
                {
                    fkOff.CommandText = "PRAGMA foreign_keys = OFF";
                    fkOff.ExecuteNonQuery();
                }

                // SQLite ATTACH DATABASE doesn't support parameter binding for the path argument,
                // so we manually escape single quotes (SQL string literal escape: '' inside '...').
                // The path itself is constructed from Path.GetTempPath() + a constant filename, but
                // Path.GetTempPath() reflects $TMPDIR / TEMP env vars and theoretically could contain
                // a single quote on user-controlled installs.
                using var attachCmd = conn.CreateCommand();
                attachCmd.CommandText = $"ATTACH DATABASE '{extractedDb.Replace("'", "''")}' AS snap";
                attachCmd.ExecuteNonQuery();

                using var tx = conn.BeginTransaction();
                try
                {
                    foreach (var table in importTables)
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

            var mediaDir = Path.Combine(_dataPath, "media");
            Directory.CreateDirectory(mediaDir);
            var extractedMedia = Path.Combine(tempDir, "media");
            if (Directory.Exists(extractedMedia))
            {
                foreach (var f in Directory.GetFiles(extractedMedia, "*.enc"))
                    File.Copy(f, Path.Combine(mediaDir, Path.GetFileName(f)), overwrite: true);
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

    public async Task ApplyNetworkRestoreAsync(
        string snapshotFilePath,
        BeeMemoryBank.Sync.RestoreNetworkEventPayload restorePayload,
        BeeMemoryBank.Core.Models.SyncEvent restoreEvent)
    {
        if (_replayShieldRepo == null)
            throw new InvalidOperationException("Replay shield repository is required for network restore");

        if (!File.Exists(snapshotFilePath))
            throw new FileNotFoundException($"Snapshot file not found: {snapshotFilePath}");

        var dbPath = Path.Combine(_dataPath, DbFileName);
        var backupPath = dbPath + $".backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var backupCreated = false;

        // Block until any in-flight CompactionService.CompactAsync finishes, then hold the lock
        // for the duration of the import. CompactionService.CompactAsync uses WaitAsync(0) and
        // bails out on contention, so it will simply skip while we hold this — no deadlock.
        await HeavyOperationLock.Instance.WaitAsync();
        try
        {
        try
        {
            var snapSize = new FileInfo(snapshotFilePath).Length;
            var tempDriveInfo = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
            var dataDriveInfo = new DriveInfo(Path.GetPathRoot(_dataPath)!);
            
            // Need space to extract payload.sqlite + media, plus some buffer
            var requiredBytes = snapSize * 2;
            
            if (tempDriveInfo.AvailableFreeSpace < requiredBytes)
                throw new InvalidOperationException($"Insufficient disk space in temp for restore: need ~{requiredBytes / (1024 * 1024)}MB");
            if (dataDriveInfo.AvailableFreeSpace < requiredBytes)
                throw new InvalidOperationException($"Insufficient disk space in data for restore: need ~{requiredBytes / (1024 * 1024)}MB");
        }
        catch (ArgumentException) { /* DriveInfo can fail on unusual paths */ }
        catch (DriveNotFoundException) { }

        if (File.Exists(dbPath))
        {
            File.Copy(dbPath, backupPath, overwrite: false);
            backupCreated = true;
            _logger?.LogInformation("Created pre-restore DB backup at {Path}", backupPath);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-network-restore-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            
            await ExtractTarGzAsync(snapshotFilePath, tempDir, new FileInfo(snapshotFilePath).Length);

            var extractedDb = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Snapshot does not contain a database file");

            await DecryptDbIfNeededAsync(extractedDb);

            // Stage media files into media.staging/ adjacent to the real media dir.
            // Only after the DB transaction commits do we atomically swap.
            var mediaDir = Path.Combine(_dataPath, "media");
            var mediaStagingDir = Path.Combine(_dataPath, "media.staging");
            if (Directory.Exists(mediaStagingDir))
            {
                try { Directory.Delete(mediaStagingDir, true); } catch { /* ignore stale staging */ }
            }
            Directory.CreateDirectory(mediaStagingDir);
            var extractedMediaEarly = Path.Combine(tempDir, "media");
            if (!Directory.Exists(extractedMediaEarly))
            {
                var alt = Path.Combine(tempDir, "originator", "media");
                if (Directory.Exists(alt)) extractedMediaEarly = alt;
            }
            if (Directory.Exists(extractedMediaEarly))
            {
                foreach (var f in Directory.GetFiles(extractedMediaEarly, "*.enc"))
                    File.Copy(f, Path.Combine(mediaStagingDir, Path.GetFileName(f)), overwrite: true);
            }

            var importTables = new[]
            {
                "tbl_folder", "tbl_article", "tbl_article_body", "tbl_concept_tag",
                "tbl_article_concept_tag", "tbl_concept_tag_edge", "tbl_media",
                "tbl_tombstone", "tbl_conflict_version", "tbl_projection_matrix",
                "tbl_comment", "tbl_article_version"
            };

            using (var conn = (SqliteConnection)_connFactory.CreateConnection())
            {
                using (var fkOff = conn.CreateCommand())
                {
                    fkOff.CommandText = "PRAGMA foreign_keys = OFF";
                    fkOff.ExecuteNonQuery();
                }

                // SQLite ATTACH DATABASE doesn't support parameter binding for the path argument,
                // so we manually escape single quotes (SQL string literal escape: '' inside '...').
                // The path itself is constructed from Path.GetTempPath() + a constant filename, but
                // Path.GetTempPath() reflects $TMPDIR / TEMP env vars and theoretically could contain
                // a single quote on user-controlled installs.
                using var attachCmd = conn.CreateCommand();
                attachCmd.CommandText = $"ATTACH DATABASE '{extractedDb.Replace("'", "''")}' AS snap";
                attachCmd.ExecuteNonQuery();

                using var tx = conn.BeginTransaction();
                try
                {
                    foreach (var table in importTables)
                    {
                        using var checkCmd = conn.CreateCommand();
                        checkCmd.Transaction = tx;
                        checkCmd.CommandText = "SELECT COUNT(*) FROM snap.sqlite_master WHERE type='table' AND name=@t";
                        checkCmd.Parameters.Add(new SqliteParameter("t", table));
                        var exists = Convert.ToInt64(checkCmd.ExecuteScalar());
                        if (exists == 0) continue;

                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = $"DELETE FROM [{table}]";
                        delCmd.ExecuteNonQuery();

                        using var importCmd = conn.CreateCommand();
                        importCmd.Transaction = tx;
                        importCmd.CommandText = $"INSERT INTO [{table}] SELECT * FROM snap.[{table}]";
                        importCmd.ExecuteNonQuery();
                    }

                    using (var checkEventCmd = conn.CreateCommand())
                    {
                        checkEventCmd.Transaction = tx;
                        checkEventCmd.CommandText = "SELECT COUNT(*) FROM snap.sqlite_master WHERE type='table' AND name='tbl_event'";
                        if (Convert.ToInt64(checkEventCmd.ExecuteScalar()) > 0)
                        {
                            // Append snapshot's events to local log without erasing local-only events.
                            // INSERT OR IGNORE skips duplicates by event_id PK; sequence_num is AUTOINCREMENT
                            // so newly imported rows get fresh local sequence numbers.
                            using var importEventCmd = conn.CreateCommand();
                            importEventCmd.Transaction = tx;
                            importEventCmd.CommandText = @"
                                INSERT OR IGNORE INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at, entity_id, article_id)
                                SELECT event_id, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at, entity_id, article_id FROM snap.tbl_event";
                            importEventCmd.ExecuteNonQuery();
                        }
                    }

                    using (var maxSeqCmd = conn.CreateCommand())
                    {
                        maxSeqCmd.Transaction = tx;
                        maxSeqCmd.CommandText = "SELECT COALESCE(MAX(sequence_num), 0) FROM tbl_event";
                        long maxSeq = Convert.ToInt64(maxSeqCmd.ExecuteScalar());
                        long newSeq = maxSeq + 1;

                        using var insertRestoreEventCmd = conn.CreateCommand();
                        insertRestoreEventCmd.Transaction = tx;
                        // OR IGNORE: when this node is the originator, /restore-network endpoint
                        // already AppendAsync'd the event before triggering background apply, so
                        // the event row already exists.  Peers (where event arrived via sync) also
                        // already wrote it via EventApplier.ApplyAsync. Either way we don't want
                        // to fail on the UNIQUE(event_id) constraint — the event is already
                        // recorded; we just want to make sure it's there.
                        insertRestoreEventCmd.CommandText = @"
                            INSERT OR IGNORE INTO tbl_event (event_id, sequence_num, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at)
                            VALUES (@eventId, @seq, @nodeId, @lamportTs, @eventType, @payload, @signature, @protocolVersion, @createdAt)";
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("eventId", restoreEvent.EventId));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("seq", newSeq));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("nodeId", restoreEvent.NodeId.ToString()));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("lamportTs", restoreEvent.LamportTs));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("eventType", restoreEvent.EventType));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("payload", JsonSerializer.Serialize(restorePayload)));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("signature", (object?)restoreEvent.Signature ?? Array.Empty<byte>()));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("protocolVersion", restoreEvent.ProtocolVersion));
                        insertRestoreEventCmd.Parameters.Add(new SqliteParameter("createdAt", restoreEvent.CreatedAt.ToString("O")));
                        insertRestoreEventCmd.ExecuteNonQuery();
                    }

                    using (var resetPushCmd = conn.CreateCommand())
                    {
                        resetPushCmd.Transaction = tx;
                        resetPushCmd.CommandText = "DELETE FROM tbl_sync_push_position";
                        resetPushCmd.ExecuteNonQuery();
                    }

                    using (var resetSyncCmd = conn.CreateCommand())
                    {
                        resetSyncCmd.Transaction = tx;
                        resetSyncCmd.CommandText = @"
                            UPDATE tbl_sync_position SET last_sequence_num = (
                                SELECT COALESCE(MAX(sequence_num), 0)
                                FROM tbl_event
                                WHERE node_id = tbl_sync_position.remote_node_id
                            )";
                        resetSyncCmd.ExecuteNonQuery();
                    }

                    if (_whitelistRepo != null)
                    {
                        var peers = await _whitelistRepo.GetAllActiveAsync();
                        var nowStr = DateTime.UtcNow.ToString("O");
                        // Inline UPSERT inside the same transaction/connection. Calling the
                        // repository method here would open a SECOND connection while we still
                        // hold a write transaction on `conn` → SQLite "database is locked"
                        // (Error 5). Fixes regression where peer-applied network-restore failed
                        // mid-transaction.
                        foreach (var peer in peers)
                        {
                            using var shieldCmd = conn.CreateCommand();
                            shieldCmd.Transaction = tx;
                            shieldCmd.CommandText = @"
                                INSERT INTO tbl_restore_replay_shield
                                    (peer_node_id, ignore_events_before_lamport_ts, shield_event_id, created_at)
                                VALUES
                                    (@peerNodeId, @ts, @evId, @now)
                                ON CONFLICT(peer_node_id) DO UPDATE SET
                                    ignore_events_before_lamport_ts = excluded.ignore_events_before_lamport_ts,
                                    shield_event_id = excluded.shield_event_id,
                                    created_at = excluded.created_at";
                            shieldCmd.Parameters.Add(new SqliteParameter("peerNodeId", peer.NodeId.ToString()));
                            shieldCmd.Parameters.Add(new SqliteParameter("ts", restoreEvent.LamportTs));
                            shieldCmd.Parameters.Add(new SqliteParameter("evId", restoreEvent.EventId.ToString()));
                            shieldCmd.Parameters.Add(new SqliteParameter("now", nowStr));
                            shieldCmd.ExecuteNonQuery();
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
                                $"Foreign key violations after snapshot restore: {string.Join(", ", violations)}");
                        }
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    if (Directory.Exists(mediaStagingDir))
                    {
                        try { Directory.Delete(mediaStagingDir, true); } catch { /* ignore */ }
                    }
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

            // Atomic media swap after successful DB commit.
            var mediaOldDir = Path.Combine(_dataPath, "media.old");
            if (Directory.Exists(mediaStagingDir) && Directory.GetFiles(mediaStagingDir, "*.enc").Length > 0)
            {
                if (Directory.Exists(mediaOldDir))
                {
                    try { Directory.Delete(mediaOldDir, true); } catch { /* ignore stale old */ }
                }
                if (Directory.Exists(mediaDir))
                {
                    try { Directory.Move(mediaDir, mediaOldDir); } catch { /* first restore, no existing dir */ }
                }
                Directory.Move(mediaStagingDir, mediaDir);
                if (Directory.Exists(mediaOldDir))
                {
                    try { Directory.Delete(mediaOldDir, true); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to sweep media.old/ after atomic swap"); }
                }
            }
            else
            {
                if (Directory.Exists(mediaStagingDir))
                {
                    try { Directory.Delete(mediaStagingDir, true); } catch { /* ignore */ }
                }
            }

            CleanupOrphanMediaFiles();

            if (backupCreated && File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { /* ignore */ }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        }
        catch (Exception ex) when (backupCreated && File.Exists(backupPath))
        {
            _logger?.LogWarning(ex, "Network restore failed — attempting to restore DB from backup {Path}", backupPath);
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Copy(backupPath, dbPath, overwrite: true);
                _logger?.LogInformation("DB restored from backup after failed network restore");
            }
            catch (Exception restoreEx)
            {
                _logger?.LogError(restoreEx, "Failed to restore DB from backup — manual recovery needed");
            }
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
            throw;
        }
        finally
        {
            HeavyOperationLock.Instance.Release();
        }
    }

    // Domain separation tags. These prepend the signed bytes so a signature produced for
    // one purpose can NEVER verify against a different purpose, even if the underlying
    // hashes happen to collide. Forms a "fail-closed" structural defense against verifier
    // confusion bugs in future code.
    //
    // EMBEDDED tag — for `manifest.json.sig` inside tar.gz. Signs the manifest bytes only;
    // file integrity follows transitively from manifest's per-file SHA256 entries.
    //
    // SIDECAR tag — for `<file>.tar.gz.sig` next to the archive. Signs SHA256(manifest||file).
    // Used by sync-export RestoreForJoinAsync.
    //
    // Format: ASCII tag + single 0x00 separator + payload. The 0x00 prevents any
    // collision via prefix-extension since 0x00 cannot appear in our ASCII tag alphabet.
    private static readonly byte[] DomainTagEmbedded = "BMB-MANIFEST-V1\0"u8.ToArray();
    private static readonly byte[] DomainTagSidecar  = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    private static readonly byte[] DbEncryptionAad = "bmb-snap-db-v1"u8.ToArray();
    private static readonly byte[] DbEncryptionAadV2 = "bmb-snap-db-v2"u8.ToArray();

    private async Task DecryptDbIfNeededAsync(string extractedDbPath)
    {
        var probe = new byte[Math.Min(64, new FileInfo(extractedDbPath).Length)];
        await using (var probeStream = File.OpenRead(extractedDbPath))
        {
            await probeStream.ReadExactlyAsync(probe, 0, probe.Length);
        }
        if (!IsDbEncrypted(probe))
            return;

        if (_sessionService is not { IsUnlocked: true })
            throw new InvalidOperationException(
                "Snapshot database is encrypted but the session is locked. Unlock the vault before restoring.");

        var masterDek = _sessionService.GetMasterDek();
        try
        {
            await DecryptDbFileAsync(extractedDbPath, masterDek);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    /// <summary>
    /// Sign payload with the node's Ed25519 private key. For legacy v=0 rows (plaintext seed)
    /// works without a session. For v=1 rows requires an unlocked SessionService to decrypt
    /// the wrapped seed. Throws InvalidOperationException with a clear message if the version
    /// is v=1 and no unlocked session is available.
    /// </summary>
    private byte[] SignWithIdentityAuto(NodeIdentity nodeIdentity, byte[] payload)
    {
        if (nodeIdentity.Ed25519PrivateKeyV == 0)
        {
            // Legacy plaintext: no session needed; pass an empty masterDek (helper does not use
            // it on the v=0 branch).
            return NodeIdentityCrypto.SignWithIdentity(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, Array.Empty<byte>(), payload);
        }

        if (_sessionService is not { IsUnlocked: true })
            throw new InvalidOperationException("Session must be unlocked to sign with v=1 (encrypted) node identity.");
        var masterDek = _sessionService.GetMasterDek();
        try
        {
            return NodeIdentityCrypto.SignWithIdentity(
                nodeIdentity.Ed25519PrivateKey, nodeIdentity.Ed25519PrivateKeyIV, nodeIdentity.Ed25519PrivateKeyV,
                nodeIdentity.NodeId, masterDek, payload);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    internal static bool IsDbEncrypted(byte[] blob)
    {
        return IsDbEncryptedV2(blob) || IsDbEncryptedV1(blob);
    }

    internal static bool IsDbEncryptedV2(byte[] blob)
    {
        return blob.Length >= DbEncryptionOverheadV2
               && Encoding.ASCII.GetString(blob, 0, 6) == DbEncryptionMagicV2;
    }

    internal static bool IsDbEncryptedV1(byte[] blob)
    {
        return blob.Length >= DbEncryptionOverheadV1
               && Encoding.ASCII.GetString(blob, 0, 6) == DbEncryptionMagicV1;
    }

    internal static async Task EncryptDbFileAsync(string dbPath, byte[] masterDek)
    {
        var dbBytes = await File.ReadAllBytesAsync(dbPath);
        try
        {
            if (dbBytes.Length > MaxEncryptableDbSize)
                throw new InvalidOperationException(
                    $"Database file is {dbBytes.Length / (1024.0 * 1024.0):F1} MB, exceeds the 2 GB encryption limit. Use a smaller database.");
            var salt = SecureRandom.GetBytes(16);
            var snapDek = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek, 32, salt, DbEncryptionAadV2);
            try
            {
                var iv = SecureRandom.GetBytes(12);
                var ct = new byte[dbBytes.Length];
                var tag = new byte[16];
                using (var gcm = new AesGcm(snapDek, 16))
                {
                    gcm.Encrypt(iv, dbBytes, ct, tag, DbEncryptionAadV2);
                }
                await using var fs = File.Create(dbPath);
                fs.Write(Encoding.ASCII.GetBytes(DbEncryptionMagicV2));
                fs.Write(salt);
                fs.Write(iv);
                fs.Write(tag);
                fs.Write(ct);
            }
            finally
            {
                Array.Clear(snapDek);
            }
        }
        finally
        {
            Array.Clear(dbBytes);
        }
    }

    internal static async Task DecryptDbFileAsync(string dbPath, byte[] masterDek)
    {
        var blob = await File.ReadAllBytesAsync(dbPath);
        if (!IsDbEncrypted(blob))
            return;

        byte[] pt;
        if (IsDbEncryptedV2(blob))
        {
            var salt = blob[6..22];
            var iv = blob[22..34];
            var tag = blob[34..50];
            var ct = blob[50..];
            var snapDek = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterDek, 32, salt, DbEncryptionAadV2);
            try
            {
                pt = new byte[ct.Length];
                using var gcm = new AesGcm(snapDek, 16);
                gcm.Decrypt(iv, ct, tag, pt, DbEncryptionAadV2);
            }
            finally
            {
                Array.Clear(snapDek);
            }
        }
        else
        {
            var iv = blob[6..18];
            var tag = blob[18..34];
            var ct = blob[34..];
            pt = new byte[ct.Length];
            using var gcm = new AesGcm(masterDek, 16);
            gcm.Decrypt(iv, ct, tag, pt, DbEncryptionAad);
        }

        await File.WriteAllBytesAsync(dbPath, pt);
        Array.Clear(pt);
        Array.Clear(blob);
    }

    public static byte[] BuildSigPayloadEmbedded(byte[] manifestBytes)
    {
        var buf = new byte[DomainTagEmbedded.Length + manifestBytes.Length];
        Buffer.BlockCopy(DomainTagEmbedded, 0, buf, 0, DomainTagEmbedded.Length);
        Buffer.BlockCopy(manifestBytes, 0, buf, DomainTagEmbedded.Length, manifestBytes.Length);
        return buf;
    }

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

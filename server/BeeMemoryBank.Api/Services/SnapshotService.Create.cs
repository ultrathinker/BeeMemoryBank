using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Exceptions;
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
                // Typed, not a message the caller has to recognise: the network-restore flow routes
                // a disk-space refusal to a "continue without a backup?" admin prompt and every
                // other refusal to a plain failure. It used to tell them apart with
                // Message.Contains("disk space"), which made this literal a wire contract.
                if (tempDriveInfo.AvailableFreeSpace < requiredBytes)
                    throw new InsufficientDiskSpaceException(
                        $"Insufficient disk space for snapshot: need ~{requiredBytes / (1024 * 1024)}MB in {tempDriveInfo.Name}, have {tempDriveInfo.AvailableFreeSpace / (1024 * 1024)}MB");
                if (snapshotsDriveInfo.AvailableFreeSpace < requiredBytes)
                    throw new InsufficientDiskSpaceException(
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
            if (encryptDb)
            {
                // Refuse rather than silently downgrade. This used to fall through to "write it
                // unencrypted" whenever the vault happened to be locked, which is precisely when
                // nobody is watching — a snapshot taken right after a restart, or by a scheduled
                // job, landed on disk as a plain SQLite file holding every article body, key slot
                // and user row. The caller asked for an encrypted snapshot; if that is impossible
                // the honest answer is an error, not a weaker file with the same name.
                //
                // Callers that legitimately need plaintext pass encryptDb: false and say why —
                // the join snapshot does, because the joining node has no master DEK yet and the
                // bytes travel over an authenticated TLS channel instead.
                //
                // A NULL session service is a different thing from a locked one: it means this
                // instance was constructed without any encryption capability at all, which only
                // test scaffolding does — the composition root resolves it with GetRequiredService
                // so a running node cannot end up in that state.
                if (_sessionService is { IsUnlocked: false })
                    throw new InvalidOperationException(
                        "Cannot create an encrypted snapshot while the vault is locked. Unlock it first, " +
                        "or request an unencrypted snapshot explicitly.");

                if (_sessionService != null)
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

            // Second-resolution timestamps collide in practice: the pre-restore safety backup is
            // created within the same second as an adjacent snapshot often enough to matter, and
            // the second file then overwrote the first under an identical name — silently
            // destroying the one copy that exists to be restored if the restore goes wrong.
            // Disambiguate with a counter rather than widening the timestamp, so file names keep
            // the shape operators and older snapshots already have.
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"bmb-snapshot-{timestamp}.tar.gz";
            var filePath = Path.Combine(SnapshotsDir, fileName);
            for (var dedupe = 2; File.Exists(filePath); dedupe++)
            {
                fileName = $"bmb-snapshot-{timestamp}-{dedupe}.tar.gz";
                filePath = Path.Combine(SnapshotsDir, fileName);
            }

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
}

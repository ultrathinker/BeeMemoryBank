using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class SnapshotService
{
    /// <summary>The clock of the snapshot file names, to the second. The integration tests set it so that two snapshots share a second.</summary>
    internal Func<DateTime> SnapshotNameClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The fingerprint of the master DEK a join snapshot is sealed under, or null while the vault is locked (the join cache key).</summary>
    public string? JoinKeyFingerprint() => _keys?.GetJoinKeyFingerprint();

    /// <summary>
    /// Signs with the node identity through the key operations this instance was given. Without any (test scaffolding) only an
    /// identity that needs no key signs - a legacy plaintext seed (v=0), or a v=2 one whose seed is not there and says so - exactly as
    /// the code this replaced did without a session.
    /// </summary>
    private byte[] SignWithIdentity(NodeIdentity nodeIdentity, byte[] payload)
    {
        if (_keys is not null) return _keys.SignWithIdentity(nodeIdentity, payload);
        if (nodeIdentity.Ed25519PrivateKeyV == 0) return Ed25519Signer.Sign(nodeIdentity.Ed25519PrivateKey, payload);
        if (nodeIdentity.Ed25519PrivateKeyV == NodeIdentityCrypto.ExternalKeyVersion) return NodeIdentityCrypto.SignWithExternalSeed(null, payload);
        throw new InvalidOperationException("Session must be unlocked to sign with v=1 (encrypted) node identity.");
    }

    /// <summary>
    /// Create a snapshot of the current node's database (and media).
    /// </summary>
    /// <param name="filterSecrets">Strip identity/keys/secrets — for distribution to peers. Local backups keep them.</param>
    /// <param name="sign">Embed Ed25519 signature of (manifest || file) by this node. Default true:
    /// every snapshot is signed by its creator so it can later participate in network-wide restore
    /// without manual re-signing. The signature is provenance proof, never a secret.</param>
    /// <param name="cpSequenceNum">Lamport checkpoint sequence number — set by compaction/sync paths.</param>
    /// <param name="additions">Package-specific changes on top of a peer snapshot — the blind
    /// package (plan 4.2) is the one caller. See <see cref="SnapshotAdditions"/>.</param>
    /// <param name="outputDirectory">Where the archive is written instead of <see cref="SnapshotsDir"/>.
    /// A file there is invisible to listing, retention and delete, which only read the snapshots directory.</param>
    /// <param name="sealForJoin">After activation (docs/design/step-a-spec.md 11): the database entry is a BMBCH1 container under the master DEK
    /// ("dbCipher": "bmbch1" in the manifest) instead of plain SQLite. Refuses while the vault is locked; wins over <paramref name="encryptDb"/>.</param>
    /// <param name="wipeStaging">The plain working copy goes through <c>SecureFileWipe</c> (overwritten with random bytes, then removed) instead of
    /// being truncated and deleted: the pre-encryption snapshot of a node that is about to be sealed (docs/design/step-b-spec.md 10.4 step 3). The
    /// container takes the copy's name only after the wipe. A node that is already sealed has no plain copy to wipe.</param>
    /// <remarks>
    /// On a sealed node (docs/design/step-b-spec.md 8.1, 8.3) the working copy is a <c>VACUUM INTO</c> of the keyed vault, so it is encrypted with the
    /// vault's page key at every moment; it is filtered through keyed connections, and its database entry is a container built from its plain image
    /// in memory. No plain SQLite file exists. <c>encryptDb: false</c> is refused there.
    /// </remarks>
    public async Task<SnapshotInfo> CreateAsync(
        bool filterSecrets = true,
        bool sign = true,
        long? cpSequenceNum = null,
        bool encryptDb = true,
        SnapshotAdditions? additions = null,
        string? outputDirectory = null,
        bool sealForJoin = false,
        bool wipeStaging = false)
    {
        // A sealed node never writes a plain image (spec 8.1): the only container writer it has for a join is sealForJoin.
        if (_connFactory.IsSealed && !encryptDb && !sealForJoin)
            throw new InvalidOperationException(
                "This node's database is encrypted: a snapshot of it is always encrypted too. A plain snapshot is not made.");

        var outputDir = outputDirectory ?? SnapshotsDir;
        Directory.CreateDirectory(outputDir);

        try
        {
            var dbPath = Path.Combine(_dataPath, DbFileName);
            if (File.Exists(dbPath))
            {
                var dbSize = new FileInfo(dbPath).Length;
                // The working copy is staged in the data folder (SnapshotStaging), so that is the drive it needs.
                var tempDriveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(SnapshotStaging.DirIn(_dataPath)))!);
                var snapshotsDriveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputDir))!);
                var requiredBytes = dbSize * 2;
                // Typed, not a message the caller has to recognise: the network-restore flow routes
                // a disk-space refusal to a "continue without a backup?" admin prompt and every
                // other refusal to a plain failure — the exception type is the contract; never rely
                // on message text.
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

        // Inside the data folder, owner-only, swept at the next start if this process dies before the finally below:
        // until FilterSecretsFrom has run, this is the whole database (key slots, wrapped key, password hashes).
        var tempDb = SnapshotStaging.NewFile(_dataPath);
        var plainWiped = false;
        var completed = false;
        string? archive = null;
        try
        {
            using (var conn = (SqliteConnection)_connFactory.CreateConnection())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{tempDb.Replace("'", "''")}'";
                cmd.ExecuteNonQuery();
            }

            // Every change to the copy comes BEFORE its final compaction. A row updated or deleted
            // afterwards would only be gone from queries: SQLite leaves the old bytes in free and
            // overflow pages, and the file goes into the archive as it is (review L-stage1 #1 — a
            // nulled embedding projection was still readable in the raw package).
            additions?.AdjustDb(tempDb);
            if (filterSecrets)
                FilterSecretsFrom(tempDb, additions?.KeepTables);
            else if (additions != null)
                CompactInPlace(tempDb);

            bool dbEncrypted = false;
            string? dbCipher = null;
            if (sealForJoin)
            {
                // Never a plain file: a locked vault (or a node with no key operations) is a refusal, not a downgrade.
                if (_keys is null)
                    throw new InvalidOperationException("This node cannot seal a join snapshot: it has no key operations.");
                if (_connFactory.IsSealed)
                {
                    var pageKey = _connFactory.CopyPageKey()!;
                    try { await _keys.SealJoinSealedDatabaseAsync(tempDb, pageKey); }
                    finally { Array.Clear(pageKey); }
                }
                else
                {
                    await _keys.SealJoinDatabaseAsync(tempDb);
                }
                dbEncrypted = true;
                dbCipher = "bmbch1";
            }
            else if (encryptDb)
            {
                // Refuse rather than silently downgrade: falling back to "write it unencrypted"
                // whenever the vault happens to be locked would land a plain SQLite file holding
                // every article body, key slot and user row on disk at exactly the moments
                // nobody is watching (right after a restart, scheduled job). The caller asked for
                // an encrypted snapshot; if that is impossible the honest answer is an error, not
                // a weaker file with the same name.
                //
                // Callers that legitimately need plaintext pass encryptDb: false and say why —
                // the join snapshot does, because the joining node has no master DEK yet and the
                // bytes travel over an authenticated TLS channel instead.
                //
                // NULL key operations are a different thing from a locked vault: it means this
                // instance was constructed without any encryption capability at all, which only
                // test scaffolding does — the composition roots always pass ISnapshotKeyOperations
                // so a running node cannot end up in that state. A locked vault (and a blind node,
                // which never holds a master DEK) makes EncryptDatabaseAsync throw.
                if (_keys != null)
                {
                    if (_connFactory.IsSealed)
                    {
                        // The working copy is encrypted with the vault's page key; the container is built from its plain image in memory.
                        var pageKey = _connFactory.CopyPageKey()!;
                        try { dbEncrypted = await _keys.EncryptSealedDatabaseAsync(tempDb, pageKey); }
                        finally { Array.Clear(pageKey); }
                    }
                    else if (wipeStaging)
                    {
                        dbEncrypted = await _keys.EncryptDatabaseAsync(tempDb, wipePlain: true, AfterStagingOverwrite);
                        plainWiped = dbEncrypted;
                    }
                    else
                    {
                        dbEncrypted = await _keys.EncryptDatabaseAsync(tempDb);
                    }
                }
            }

            var allFiles = new Dictionary<string, string>();

            var dbHash = await ComputeHashAsync(tempDb);
            allFiles[DbFileName] = dbHash;

            // Listed in the signed manifest like every other file, so they are covered by the
            // same signature and VerifyManifestAsync checks them on the way in.
            var extraFiles = additions?.ExtraFiles ?? new Dictionary<string, byte[]>();
            foreach (var (name, bytes) in extraFiles)
                allFiles[name] = Convert.ToHexStringLower(SHA256.HashData(bytes));

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

            if (dbCipher != null)
                manifestDict["dbCipher"] = dbCipher;

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
            // the shape operators and older snapshots already have. The counter also skips a name taken in the other snapshot folder: a restore by
            // name looks in snapshots/ first, so the same name in pre-encryption/ would open the wrong archive.
            var fileName = SnapshotFileNames.Unique(_dataPath, outputDir, SnapshotNameClock());
            var filePath = Path.Combine(outputDir, fileName);

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
                manifestSignature = SignWithIdentity(nodeIdentity, manifestPayload);
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

                foreach (var (name, bytes) in extraFiles)
                {
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = new MemoryStream(bytes)
                    });
                }

                if (manifestSignature != null)
                {
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName + ".sig")
                    {
                        DataStream = new MemoryStream(manifestSignature)
                    });
                }
            }

            archive = filePath;
            if (sign)
            {
                // Sidecar signature over (manifest || file content) for sync-export back-compat.
                var nodeIdentity = await _nodeRepo!.GetAsync()
                    ?? throw new InvalidOperationException("Node identity not found");
                var sidecarPayload = await ComputeSignaturePayloadAsync(manifestBytes, filePath);
                var sidecarSig = SignWithIdentity(nodeIdentity, sidecarPayload);
                await File.WriteAllBytesAsync($"{filePath}.sig", sidecarSig);
            }

            var fi = new FileInfo(filePath);
            completed = true;
            return new SnapshotInfo(fileName, fi.Length, fi.LastWriteTimeUtc,
                cpSequenceNum, producerNodeId, sign);
        }
        finally
        {
            // A failure before the container was written leaves the plain working copy: with wipeStaging it is overwritten before it goes.
            AfterStagingWork?.Invoke(tempDb);
            RemoveWorkingCopy(tempDb, wipe: wipeStaging && !plainWiped, completed, archive);
        }
    }

    /// <summary>
    /// Removes a working copy with its SQLite sidecars: the one cleanup of <see cref="SqliteStaging"/>, wiped when <paramref name="wipe"/> asks
    /// for it. A copy that stays is repair-required: after a completed snapshot that is the failure reported, never a snapshot made, so the
    /// archive written for it goes as well.
    /// </summary>
    private void RemoveWorkingCopy(string tempDb, bool wipe, bool completed, string? archive)
    {
        Func<string, bool>? wipeFile = wipe ? path => SecureFileWipe.TryWipe(path, AfterStagingOverwrite) : null;
        if (SqliteStaging.TryCleanup(tempDb, wipeFile)) return;
        _logger?.LogError("A working copy of a snapshot could not be removed; node repair is required.");
        if (!completed) return;
        if (archive is not null)
            foreach (var file in new[] { archive, archive + ".sig" })
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover file is no snapshot */ }
        throw new IOException("The working copy of the snapshot could not be removed. The snapshot requires repair and was not reported as made.");
    }

    /// <summary>Test seam of <c>wipeStaging</c>: called with the working copy after it was overwritten and flushed, before it is truncated.</summary>
    internal Action<string>? AfterStagingOverwrite { get; set; }

    /// <summary>Test seam: called with the working copy once its work is done and before it is removed; a test plants or holds a sidecar of it here.</summary>
    internal Action<string>? AfterStagingWork { get; set; }
}

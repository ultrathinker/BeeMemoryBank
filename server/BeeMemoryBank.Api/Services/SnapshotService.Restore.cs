using BeeMemoryBank.Core.Services;
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
    /// Tables whose absence makes an archive unrestorable rather than merely older. These are
    /// dropped by <c>FilterSecretsFrom</c>, so their absence identifies a package built for a
    /// joining peer — see the check in <see cref="RestoreAsync"/> for why that must not be
    /// restored over a live database.
    /// </summary>
    private static readonly string[] RestoreEssentialTables =
        ["tbl_key_slot", "tbl_user", "tbl_node_identity"];

    /// <summary>
    /// Peer-relationship state wiped from a standalone restore: the archive describes the
    /// ORIGINATOR's place in the network, and this node is becoming a fresh, unrelated one.
    /// </summary>
    private static readonly string[] NetworkStateTablesToWipe =
    [
        "tbl_whitelist", "tbl_sync_position", "tbl_sync_push_position",
        "tbl_restore_replay_shield", "tbl_event", "tbl_sync_quarantine"
    ];

    /// <summary>Table names present in a database file, read without pooling the handle.</summary>
    private static HashSet<string> ReadTableNames(string dbPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Pooling=False for the same reason as everywhere else in this file: the file is moved
        // or deleted moments later, and a pooled handle keeps it locked on Windows.
        using var conn = new SqliteConnection($"Data Source={dbPath.Replace("'", "''")};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
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

            var archiveTables = ReadTableNames(extractedDb);

            // Reject a peer-distribution package before it replaces the live database.
            // A snapshot created with filterSecrets:true has tbl_key_slot DROPPED, and the key
            // slots are the ONLY place the master DEK exists in wrapped form. Restoring one over
            // the live DB would therefore hand back a vault whose article bodies are ciphertext
            // that no password can ever unwrap again — strictly worse than not restoring at all.
            // Such an archive is meant for the join path (SnapshotJoinClient), which imports
            // content rows INTO a database that already has its own key slots.
            var missingEssential = RestoreEssentialTables.Where(t => !archiveTables.Contains(t)).ToList();
            if (missingEssential.Count > 0)
                throw new InvalidOperationException(
                    $"This snapshot cannot be restored: it is missing {string.Join(", ", missingEssential)}. " +
                    "It was produced as a package for a joining peer (secrets filtered out), not as a backup — " +
                    "without key slots the encrypted content could never be unlocked again. " +
                    "Restore a snapshot created from Admin → Snapshots, or join this node to a peer instead.");

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

                // Pooling=False — same reason as FilterSecretsFrom: the staging file is moved or
                // deleted right after this block, and a pooled handle would keep it locked on
                // Windows long after Dispose.
                using (var stagingConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stagingPath.Replace("'", "''")};Pooling=False"))
                {
                    stagingConn.Open();
                    using var tx = stagingConn.BeginTransaction();
                    try
                    {
                        // One statement per table, skipping the ones this archive does not have.
                        // The staging database carries the ARCHIVE's schema, not ours, and
                        // migrations have not run on it yet — tbl_sync_quarantine arrived in
                        // migration 013, so a pre-013 backup simply has no such table. A single
                        // batched DELETE aborts the whole transaction on the first "no such
                        // table" and would make every older backup unrestorable; the quarantine
                        // wipe already carried its own try/catch for exactly this reason, and
                        // every table in this list is subject to the same schema-age problem.
                        foreach (var table in NetworkStateTablesToWipe)
                        {
                            if (!archiveTables.Contains(table)) continue;
                            using var wipeCmd = stagingConn.CreateCommand();
                            wipeCmd.Transaction = tx;
                            wipeCmd.CommandText = $"DELETE FROM {table}";
                            wipeCmd.ExecuteNonQuery();
                        }

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
                // Atomically replace the live DB, retrying past transient pooled-handle locks —
                // see SwapDbFileWithRetryAsync for why clear-then-swap must be one gesture.
                await SwapDbFileWithRetryAsync(
                    () => File.Move(stagingPath, dbPath, overwrite: true),
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools,
                    _logger);

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

                await SwapDbFileWithRetryAsync(
                    () => File.Copy(extractedDb, dbPath, overwrite: true),
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools,
                    _logger);

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

                if (archiveTables.Contains("tbl_sync_push_position"))
                {
                    using var conn = _connFactory.CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM tbl_sync_push_position";
                    cmd.ExecuteNonQuery();
                }
            }

            // The database was replaced under an unchanged path, so the folder-ACL cache is now
            // describing users that no longer exist — and user ids restart at 1, so the entries
            // would be handed to whoever occupies those ids in the restored data. No restart
            // follows a restore, so nothing else clears it.
            BeeMemoryBank.Core.Services.FolderAccessService.InvalidateAll();
        }
        finally
        {
            // Never let scratch-directory cleanup decide the outcome of a restore. The database has
            // already been replaced by this point, so throwing here turns a completed restore into
            // a 500 and tells the operator it failed — and on Windows this delete genuinely can
            // fail transiently, since SQLite's -wal/-shm sidecars linger for a moment after the
            // connection that read the archive closes. The sweep on the next start clears anything
            // left behind; every other temp cleanup in this file already takes the same view.
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete restore temp dir {TempDir}", tempDir); }
            }
            HeavyOperationLock.Instance.Release();
        }
    }

    /// <summary>
    /// Replaces the live database file, tolerating the transient Windows lock a pooled background
    /// reader can hold on it. Microsoft.Data.Sqlite keeps a connection's physical handle open in the
    /// pool after <c>Dispose</c>, so even an idle background reader (sync scheduler, search-index
    /// read, WAL checkpoint) keeps the file open — and <c>File.Move</c>/<c>File.Copy</c> over a file
    /// another handle has open throws <see cref="UnauthorizedAccessException"/> /
    /// <see cref="IOException"/>. <c>ClearAllPools</c> force-closes those handles, but a
    /// <c>CreateConnection</c> the instant after re-locks the file, so the swap has to land in the
    /// freed window: clear the pool and swap with nothing in between, and retry with backoff. In
    /// normal operation the next background open is seconds away, so a retry wins immediately; the
    /// old single-shot <c>ClearAllPools + 200&#160;ms delay + swap</c> lost that race ~1-in-N on a
    /// busy node and surfaced as a bare 500. Reproduced deterministically under concurrent DB load.
    /// <para>Seams (<paramref name="clearPools"/>, <paramref name="delay"/>) exist so the retry
    /// logic is unit-tested without real files or timing — see SnapshotFileSwapRetryTests.</para>
    /// </summary>
    internal static async Task SwapDbFileWithRetryAsync(
        Action swap,
        Action clearPools,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        int maxAttempts = 10,
        Func<int, Task>? delay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Clear immediately before the swap, with nothing between them, so File.Move lands in
            // the instant the file is free rather than after a re-lock window.
            clearPools();
            try
            {
                swap();
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < maxAttempts)
            {
                logger?.LogWarning(ex,
                    "Database file swap blocked (file still held open); attempt {Attempt}/{Max}, retrying",
                    attempt, maxAttempts);
                await (delay?.Invoke(attempt) ?? Task.Delay(50 * attempt));
            }
        }
    }
}

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

            // The database was replaced under an unchanged path, so the folder-ACL cache is now
            // describing users that no longer exist — and user ids restart at 1, so the entries
            // would be handed to whoever occupies those ids in the restored data. No restart
            // follows a restore, so nothing else clears it.
            BeeMemoryBank.Core.Services.FolderAccessService.InvalidateAll();
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            HeavyOperationLock.Instance.Release();
        }
    }
}

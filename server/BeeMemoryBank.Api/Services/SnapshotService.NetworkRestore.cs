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
                // tbl_blob FIRST: article bodies and versions address their ciphertext by hash into
                // this table since migration 016. Import it after them and there is a window where
                // a body row resolves to nothing; omit it entirely and every article read on the
                // restored node returns empty content while looking perfectly healthy.
                "tbl_blob",
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
                // tbl_blob FIRST: article bodies and versions address their ciphertext by hash into
                // this table since migration 016. Import it after them and there is a window where
                // a body row resolves to nothing; omit it entirely and every article read on the
                // restored node returns empty content while looking perfectly healthy.
                "tbl_blob",
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

                    // Node-local tables that reference an imported one are NOT in importTables:
                    // they belong to this node and must survive the restore. But the import above
                    // wholesale replaced tbl_article, so any of their rows pointing at an article
                    // that the snapshot does not contain is now dangling — and the
                    // PRAGMA foreign_key_check below reports violations even with foreign_keys=OFF,
                    // which would roll the whole restore back on that node. Rolling back to a point
                    // before some articles existed is precisely what a network restore is for, so
                    // this is the common case, not an edge case.
                    foreach (var orphanTable in new[] { "tbl_favorite", "tbl_article_chunk_embedding" })
                    {
                        using var orphanCmd = conn.CreateCommand();
                        orphanCmd.Transaction = tx;
                        orphanCmd.CommandText =
                            $"DELETE FROM [{orphanTable}] WHERE article_id NOT IN (SELECT id FROM tbl_article)";
                        // Older DBs predate these tables; a restore must not fail because one is absent.
                        try { orphanCmd.ExecuteNonQuery(); } catch (SqliteException) { }
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
            // Same reason as SnapshotService.RestoreAsync: the database was replaced under an
            // unchanged path, so the process-wide folder-ACL cache now describes users from the
            // previous data set, and user ids restart at 1 in the restored one. In the finally
            // block on purpose — the rollback path above also leaves the database in a state the
            // cache was not built from.
            BeeMemoryBank.Core.Services.FolderAccessService.InvalidateAll();
            HeavyOperationLock.Instance.Release();
        }
    }
}

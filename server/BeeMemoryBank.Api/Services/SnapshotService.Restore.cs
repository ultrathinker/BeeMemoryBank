using BeeMemoryBank.Core.Services;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>What a restore did: whether this node became a new one and, if so, which.</summary>
/// <param name="Standalone">The node left its network and took a new identity.</param>
/// <param name="NewNodeId">The node id the node has now; null when it kept its identity.</param>
/// <param name="NodeDisplayName">The display name of the node (kept from the snapshot); null when it kept its identity.</param>
public sealed record SnapshotRestoreOutcome(bool Standalone, Guid? NewNodeId, string? NodeDisplayName);

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

    /// <summary>
    /// A <c>server_auto_unlock</c> key slot that arrives with a restored database is deleted in the staging copy before it replaces the live one
    /// (docs/design/step-b-spec.md 11.2 item 2): the slot is per server and data of the server that made the snapshot, and a restore never turns
    /// "start unlocked after a restart" on. The boot store retires its door at the rewrite after the swap. Nothing to do for an archive without
    /// such a row.
    /// </summary>
    private static void RemoveServerAutoUnlockSlots(SqliteConnection staged, SqliteTransaction? tx)
    {
        using var cmd = staged.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM tbl_key_slot WHERE slot_type = 'server_auto_unlock'";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The refusal for a backup whose database this version would not open, judged from its migration ledger (a release before 2.6, or a
    /// history nobody here knows) and from its structure (the same comparison the adoption makes of a 2.6.x database; for a backup that
    /// carries the baseline's ledger already nothing would repair a gap at the next start, so a gap refuses it); null when it would open it.
    /// Nothing is written to the backup, nothing is named but tables and columns and the version to use.
    /// </summary>
    private InvalidOperationException? DatabaseRefusalOf(string dbPath)
    {
        try
        {
            using var conn = OpenStaged(dbPath);
            var refusal = MigrationRunner.JudgeDatabase(conn);
            return refusal?.Reason switch
            {
                MigrationRefusal.OlderDatabase => new InvalidOperationException(
                    "This backup was made by a release before 2.6 (or by an update that did not finish), so this version cannot restore it. " +
                    "Restore it on a node that runs 2.6.1, update that node to this version, and make a new backup. Nothing was changed."),
                MigrationRefusal.UnknownHistory => new InvalidOperationException(
                    "This backup has a migration history this version does not recognise: it was made by a newer version. " +
                    "Restore it with the version that made it, or with a newer one. Nothing was changed."),
                MigrationRefusal.IncompatibleStructure => new InvalidOperationException(
                    "This backup has the migration history of version 2.6 but not its structure (" +
                    string.Join("; ", refusal!.Problems.Take(5)) + (refusal.Problems.Count > 5 ? $"; and {refusal.Problems.Count - 5} more" : "") +
                    "), so this version could not open it after the restore. It was probably damaged or edited. " +
                    "Restore another backup, one made by version 2.6.1 or later. Nothing was changed."),
                _ => null,
            };
        }
        catch (SqliteException ex)
        {
            throw new SnapshotContainerException(ex);
        }
    }

    /// <summary>Table names present in a database file, read without pooling the handle.</summary>
    private HashSet<string> ReadTableNames(string dbPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Not pooled, for the same reason as everywhere else in this file: the file is moved or deleted moments later, and a pooled handle
        // keeps it locked on Windows. Keyed on an encrypted node.
        try
        {
            using var conn = OpenStaged(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }
        catch (SqliteException ex)
        {
            // The container authenticated and the image was imported, yet its schema cannot be read: a damaged image, not a fault of this node. The same sentence as every
            // other unreadable snapshot (SQLite's text is not the answer); nothing has been changed when this is thrown.
            throw new SnapshotContainerException(ex);
        }
    }

    /// <summary>
    /// Replaces this node's database with the snapshot's. <paramref name="standaloneMode"/> makes this a new node (new id, new key
    /// pair, no peers); <paramref name="masterPassword"/> is the password the restore was confirmed with, used to find the master
    /// key of the snapshot's database when the session's own key does not open it.
    /// </summary>
    public async Task<SnapshotRestoreOutcome> RestoreAsync(
        string fileName, bool standaloneMode = false, string? masterPassword = null, CancellationToken ct = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid snapshot file name");

        // Before the archive is looked at: a node whose database is not encrypted takes no restore at all.
        RequireEncryptedNodeForRestore();

        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath)) throw new FileNotFoundException($"Snapshot {safeName} not found");

        // Coordinate with CompactionService — both flows bulk-rewrite tbl_event.
        // Lock acquisition is the very first statement inside the try, so any failure
        // between WaitAsync and the try-block (theoretical: Path.Combine throwing on a
        // poisoned $TMPDIR) cannot leak the semaphore.
        await HeavyOperationLock.Instance.WaitAsync();
        string? tempDir = null;
        var outcome = new SnapshotRestoreOutcome(standaloneMode, null, null);
        try
        {
            // In the data folder and owner-only, not the OS temp folder: the extracted database is the vault in clear.
            tempDir = SnapshotStaging.NewDirectory(_dataPath);

            await RequireNoPlainDatabaseAsync(filePath);
            await ExtractTarGzAsync(filePath, tempDir, new FileInfo(filePath).Length);

            await VerifyManifestAsync(tempDir);

            var dbPath = Path.Combine(_dataPath, "beememorybank.db");

            var extractedDb = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Snapshot does not contain a database file");

            await DecryptDbIfNeededAsync(extractedDb, ct);

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

            // This version opens the database of a 2.6.x backup (it is adopted at the next start) and nothing older, and only one whose structure
            // is the one the program reads. A backup of an earlier release, or a broken one, would replace the node and then be refused when the
            // node starts, so it is refused here, before anything is replaced.
            if (DatabaseRefusalOf(extractedDb) is { } databaseRefusal) throw databaseRefusal;

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

                // The new private key is stored the way initialization stores it: wrapped under the
                // master DEK of THIS database (the one its key slots will hand out at the next
                // unlock), with the new node id as AAD. A bare seed in a row that says "wrapped"
                // leaves the node unable to sign — sync authentication, pairing, every signed event —
                // while the restore itself reports success. Sealed before the write transaction
                // below opens, because it reads the staged file through its own connection.
                byte[] wrappedPrivKey, privKeyIv;
                int privKeyVersion;
                try
                {
                    if (_keys is null)
                        throw new InvalidOperationException(
                            "A standalone restore needs the node's key operations to store its new identity key.");
                    (wrappedPrivKey, privKeyIv, privKeyVersion) = _connFactory.IsSealed
                        ? _keys.SealIdentitySeedForRestore(stagingPath, newNodeId, privKey, masterPassword, p => OpenStaged(p))
                        : _keys.SealIdentitySeedForRestore(stagingPath, newNodeId, privKey, masterPassword);
                }
                catch
                {
                    // Nothing has been replaced yet; do not leave a half-prepared copy of the
                    // snapshot's database next to the live one.
                    try { File.Delete(stagingPath); } catch { }
                    throw;
                }
                finally
                {
                    Array.Clear(privKey);
                }

                // Not pooled — same reason as FilterSecretsFrom: the staging file is moved or
                // deleted right after this block, and a pooled handle would keep it locked on
                // Windows long after Dispose.
                using (var stagingConn = OpenStaged(stagingPath))
                {
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
                        RemoveServerAutoUnlockSlots(stagingConn, tx);
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
                                ed25519_private_key_iv = @newPrivKeyIv,
                                ed25519_private_key_v = @newPrivKeyV,
                                ed25519_public_key = @newPubKey";

                        var p1 = identityCmd.CreateParameter();
                        p1.ParameterName = "newNodeId";
                        p1.Value = newNodeId.ToString();
                        identityCmd.Parameters.Add(p1);

                        var p2 = identityCmd.CreateParameter();
                        p2.ParameterName = "newPrivKey";
                        p2.Value = wrappedPrivKey;
                        identityCmd.Parameters.Add(p2);

                        var p3 = identityCmd.CreateParameter();
                        p3.ParameterName = "newPubKey";
                        p3.Value = pubKey;
                        identityCmd.Parameters.Add(p3);

                        var p4 = identityCmd.CreateParameter();
                        p4.ParameterName = "newPrivKeyIv";
                        p4.Value = privKeyIv;
                        identityCmd.Parameters.Add(p4);

                        var p5 = identityCmd.CreateParameter();
                        p5.ParameterName = "newPrivKeyV";
                        p5.Value = privKeyVersion;
                        identityCmd.Parameters.Add(p5);

                         identityCmd.ExecuteNonQuery();

                         using var nameCmd = stagingConn.CreateCommand();
                         nameCmd.Transaction = tx;
                         nameCmd.CommandText = "SELECT display_name FROM tbl_node_identity LIMIT 1";
                         outcome = outcome with { NewNodeId = newNodeId, NodeDisplayName = nameCmd.ExecuteScalar() as string };

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
                // On an encrypted node the boot store learns the restored doors first and is rewritten after the swap (spec 8.4).
                await using var sealedSwap = await BeginSealedSwapAsync(stagingPath, masterPassword);
                // Atomically replace the live DB behind the connection barrier, retrying past transient pooled-handle
                // locks — see ReplaceLiveDatabaseAsync and SwapDbFileWithRetryAsync.
                await ReplaceLiveDatabaseAsync(() => File.Move(stagingPath, dbPath, overwrite: true));

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

                using (var extractedConn = OpenStaged(extractedDb))
                    RemoveServerAutoUnlockSlots(extractedConn, tx: null);
                await using var sealedSwapNs = await BeginSealedSwapAsync(extractedDb, masterPassword);
                await ReplaceLiveDatabaseAsync(() => File.Copy(extractedDb, dbPath, overwrite: true));

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
            return outcome;
        }
        finally
        {
            // The extracted database is the vault in clear, so its folder must be gone before the restore is reported. The database has
            // already been replaced by this point: a folder that stays (SQLite's -wal/-shm sidecars can linger for a moment after the
            // archive's connection closes, which the retries cover) is a repair-required failure, and a sealed gate is closed for it
            // (CleanupStagingOrFailAsync). It is never a restore reported as done.
            try
            {
                if (tempDir != null)
                    await CleanupStagingOrFailAsync(tempDir);
            }
            finally
            {
                HeavyOperationLock.Instance.Release();
            }
        }
    }
}

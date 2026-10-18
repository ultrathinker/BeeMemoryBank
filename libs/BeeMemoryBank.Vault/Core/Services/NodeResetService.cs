using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Host-specific cleanup that runs after the vault database has been wiped by
/// <see cref="NodeResetService"/>. The Api registers one for chat.db (which lives next to the
/// vault but is owned by the Api project, so Core cannot clear it directly); the CLI registers
/// none. Hooks are best-effort: a failure is logged and never undoes or aborts the vault wipe,
/// which has already committed by the time they run.
/// </summary>
public interface INodeResetHook
{
    Task AfterVaultWipedAsync(CancellationToken ct);
}

public enum NodeResetOutcome
{
    /// <summary>The node has no identity row — there is nothing to reset.</summary>
    NotInitialized,
    /// <summary>The supplied master password did not open any permitted key slot.</summary>
    InvalidPassword,
    /// <summary>Everything was cleared; the node must go through Setup (init or join) again.</summary>
    Done,
}

/// <param name="WasSealed">The node's database was encrypted: it is a plain, empty node now (docs/design/step-b-spec.md 13.1) and its host must create the schema again.</param>
public sealed record NodeResetResult(NodeResetOutcome Outcome, Guid? OldNodeId = null, bool WasSealed = false);

/// <summary>
/// Wipes this node back to the pre-Setup state: every content and identity table in the vault
/// database, encrypted media files, and whatever the registered <see cref="INodeResetHook"/>s own.
/// Shared by the API endpoint (superadmin, from the Admin page) and the CLI (<c>bmb init reset</c>,
/// the host-only path when nobody can sign in to the web UI any more) so the two cannot drift apart
/// on what "reset" means.
/// <para>
/// A sealed node (docs/design/step-b-spec.md 13.1) is reset keyed, with the master password (a closed database is opened for the action only);
/// then the gate closes, the boot store and the encrypted databases - empty by now - are deleted, and the node is an uninitialized plain node.
/// <c>snapshots/</c> stays.
/// </para>
/// </summary>
public sealed class NodeResetService(
    IDbConnectionFactory connFactory,
    INodeIdentityRepository nodeRepo,
    SessionService session,
    MaintenanceModeService maintenance,
    IEnumerable<INodeResetHook> hooks,
    ILogger<NodeResetService> logger,
    string dataPath,
    VaultGate? gate = null)
{
    /// <param name="masterPassword">Re-authentication. Verified against the same key-slot policy as
    /// an unlock (superadmin "user" slots, recovery, legacy) but WITHOUT unlocking the shared
    /// session: the vault is about to be destroyed, and an unlock would also fire the post-unlock
    /// catch-up tasks (DEK-rotation retry, restore retry, identity-key migration) to race the wipe.</param>
    /// <param name="initiatedBy">Free-text origin for the audit trail — a masked remote IP for the
    /// API, <c>"cli"</c> for the command line.</param>
    public async Task<NodeResetResult> ResetAsync(string masterPassword, string initiatedBy, CancellationToken ct = default)
    {
        var sealedNode = gate is { IsSealed: true };
        IAsyncDisposable? lease = null;
        try
        {
            if (sealedNode && !gate!.IsUsable)
            {
                // A closed database: the password opens it for this action, and no session is unlocked (spec 13.1).
                lease = await session.OpenForPasswordAsync(masterPassword);
                if (lease is null) return new NodeResetResult(NodeResetOutcome.InvalidPassword);
            }

            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return new NodeResetResult(NodeResetOutcome.NotInitialized);

            if (lease is null && !await session.VerifyMasterPasswordAsync(masterPassword))
                return new NodeResetResult(NodeResetOutcome.InvalidPassword);

            // AUDIT: this is the single most destructive operation in the product, and the wipe below
            // deletes tbl_audit_log along with everything else — a record that lived only inside
            // beememorybank.db could never survive the event it describes. Write a durable trail BEFORE
            // touching anything: an append-only file in the data directory (outside the SQLite file
            // entirely) plus a Warning-level structured log line for whatever sink the deployment has.
            // Best-effort — a failure to record the trail must never block the reset itself, or the
            // audit mechanism becomes a new way to lock an admin out of resetting a compromised node.
            var resetAt = DateTime.UtcNow;
            try
            {
                var auditLine = $"{resetAt:O} node_reset old_node_id={identity.NodeId} " +
                    $"old_display_name=\"{identity.DisplayName}\" old_node_created_at={identity.CreatedAt:O} " +
                    $"initiated_by={initiatedBy}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(dataPath, "reset-audit.log"), auditLine);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write reset-audit.log (continuing with the reset)");
            }
            logger.LogWarning(
                "NODE RESET initiated: old_node_id={NodeId} old_display_name={DisplayName} initiated_by={InitiatedBy}",
                identity.NodeId, identity.DisplayName, initiatedBy);

            maintenance.Enter("Resetting node...");
            // A sealed database is wiped keyed: the lock of the session closes the gate, so there it comes after the wipe.
            if (!sealedNode) session.Lock();
            try
            {
                WipeVaultDatabase();

                var mediaDir = Path.Combine(dataPath, "media");
                if (Directory.Exists(mediaDir))
                    foreach (var f in Directory.GetFiles(mediaDir, "*.enc")) File.Delete(f);

                foreach (var hook in hooks)
                {
                    try { await hook.AfterVaultWipedAsync(ct); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Reset hook {Hook} failed (non-fatal; the vault wipe has already committed)",
                            hook.GetType().Name);
                    }
                }

                if (sealedNode)
                {
                    if (lease is not null)
                    {
                        await lease.DisposeAsync();
                        lease = null;
                    }
                    await ReturnToPlainAsync(ct);
                }

                // The folder-ACL cache is process-wide and keyed by (database, user id). The database
                // path does not change across a reset but the user ids do — they restart at 1 — so a
                // node re-initialized inside the cache TTL would hand the new account the wiped
                // account's permissions.
                FolderAccessService.InvalidateAll();

                // The node is empty again: a setup, join or restore that was waiting for its encryption has nothing left to wait for.
                BeeMemoryBank.AppPaths.SealIntent.Complete(dataPath);

                return new NodeResetResult(NodeResetOutcome.Done, identity.NodeId, sealedNode);
            }
            finally
            {
                maintenance.Exit();
            }
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// Takes back a setup, join or restore that failed after it began (its seal intent is pending) and the node is not encrypted: every content and identity
    /// table is emptied, the media files and what the hooks own go, and the intent ends - the node is the empty node it was before, and the flow is run
    /// again. No password is asked: nothing here is a node yet, and the method is not reachable from any route (the flows call it on their own failure).
    /// Returns false when nothing was pending, when the pending operation is not the calling flow's own (<see cref="BeeMemoryBank.AppPaths.SealIntent.OwnsPending"/>:
    /// a flow that failed before it began, or whose begin was refused, never takes back what another operation is writing), when the node is encrypted (then the
    /// encryption, not this, decides), or when the wipe itself failed; the intent then stays and the next start wipes what is in the open
    /// (<see cref="BeeMemoryBank.AppPaths.SealIntent"/>).
    /// </summary>
    public async Task<bool> DiscardPendingSetupAsync(string reason, CancellationToken ct = default)
    {
        if (!BeeMemoryBank.AppPaths.SealIntent.IsPending(dataPath) || !BeeMemoryBank.AppPaths.SealIntent.OwnsPending(dataPath) || gate is { IsSealed: true }) return false;
        logger.LogWarning("A setup of this node failed ({Reason}); what it wrote in the open is being removed.", reason);
        maintenance.Enter("Taking back the setup...");
        try
        {
            session.Lock();
            WipeVaultDatabase();
            var mediaDir = Path.Combine(dataPath, "media");
            if (Directory.Exists(mediaDir))
                foreach (var f in Directory.GetFiles(mediaDir, "*.enc")) File.Delete(f);
            foreach (var hook in hooks)
            {
                try { await hook.AfterVaultWipedAsync(ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Reset hook {Hook} failed while taking back a setup (non-fatal)", hook.GetType().Name); }
            }
            FolderAccessService.InvalidateAll();
            return BeeMemoryBank.AppPaths.SealIntent.Complete(dataPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The setup could not be taken back ({Type}); the next start removes what it wrote.", ex.GetType().Name);
            return false;
        }
        finally
        {
            maintenance.Exit();
        }
    }

    /// <summary>
    /// The end of the reset of a sealed node (spec 13.1): the gate closes (the boot store is not rewritten: it is about to go), the session forgets
    /// its key, the boot store and the two databases - empty tables now - are deleted, and the gate is a plain gate again. Nothing here removes
    /// <c>snapshots/</c> or anything else.
    /// </summary>
    private async Task ReturnToPlainAsync(CancellationToken ct)
    {
        await gate!.CloseAsync(VaultGate.ResetReason, ct);
        session.Lock();
        SqliteConnection.ClearAllPools();
        foreach (var name in new[] { VaultStoreFiles.Boot, VaultStoreFiles.Database, VaultStoreFiles.Chat })
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var path = Path.Combine(dataPath, name + suffix);
                if (File.Exists(path)) File.Delete(path);
            }
        gate.SwitchToPlain();
    }

    private void WipeVaultDatabase()
    {
        using var conn = connFactory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        Exec(conn, "PRAGMA foreign_keys = OFF");
        Exec(conn, "PRAGMA secure_delete = ON");
        using (var tx = conn.BeginTransaction())
        {
            // Enumerate every real content table from the LIVE schema instead of hand-maintaining a
            // list here. A hand list silently rots: it can name a table that does not exist and,
            // worse, OMIT a table a later migration adds — e.g. a surviving tbl_remote_api_token
            // hands a pre-reset bmbrt_ remote token a live read path into the NEW vault once Setup
            // reassigns its user_id. Excluded on purpose:
            //   - sqlite_%      — SQLite's own internal bookkeeping tables.
            //   - fts_%         — FTS5 index tables and their shadow tables. AFTER INSERT/UPDATE/
            //                     DELETE triggers on the content tables keep these in sync, so
            //                     clearing the content tables empties them too; writing to an FTS5
            //                     shadow table directly is unsupported and unnecessary.
            //   - tbl_migration — migration-applied bookkeeping. Wiping it would make MigrationRunner
            //                     re-run every migration against a schema that still exists —
            //                     harmless in principle but pointless and slow.
            var tablesToWipe = new List<string>();
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.Transaction = tx;
                listCmd.CommandText = @"
                    SELECT name FROM sqlite_master
                    WHERE type = 'table'
                      AND name NOT LIKE 'sqlite_%'
                      AND name NOT LIKE 'fts_%'
                      AND name <> 'tbl_migration'
                    ORDER BY name";
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                    tablesToWipe.Add(reader.GetString(0));
            }

            foreach (var table in tablesToWipe)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                // tbl_role is the one deliberate exception: DELETE FROM would take the seeded system
                // roles (superadmin/user) with it, and nothing re-seeds them — migrations only run
                // once. Every other table is cleared unconditionally.
                delCmd.CommandText = table == "tbl_role"
                    ? "DELETE FROM tbl_role WHERE is_system = 0"
                    : $"DELETE FROM [{table}]";
                try
                {
                    delCmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    // Visible instead of an empty catch — a failure here means the reset did NOT
                    // fully clear that table, which is exactly what an admin relying on "go to
                    // Setup to rejoin" being a truly clean slate needs to know.
                    logger.LogWarning(ex, "Reset: failed to clear table {Table}", table);
                }
            }

            // The full-text indexes are external-content tables: the delete triggers above only add tombstones, so the old titles, paths and tag names stay
            // in the index segments (and so in the file) until the index is emptied by its own command.
            var fullTextIndexes = new List<string>();
            using (var listFts = conn.CreateCommand())
            {
                listFts.Transaction = tx;
                listFts.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'fts_%' AND sql LIKE 'CREATE VIRTUAL TABLE%' AND sql LIKE '%content=%'";
                using var reader = listFts.ExecuteReader();
                while (reader.Read()) fullTextIndexes.Add(reader.GetString(0));
            }
            foreach (var fts in fullTextIndexes)
            {
                using var emptyCmd = conn.CreateCommand();
                emptyCmd.Transaction = tx;
                emptyCmd.CommandText = $"INSERT INTO [{fts}]([{fts}]) VALUES ('delete-all')";
                try { emptyCmd.ExecuteNonQuery(); }
                catch (Exception ex) { logger.LogWarning(ex, "Reset: failed to empty the full-text index {Index}", fts); }
            }
            tx.Commit();
        }
        Exec(conn, "PRAGMA foreign_keys = ON");
        Exec(conn, "VACUUM");
        // In WAL mode the vacuumed file is in the log until a checkpoint: the pages of the wiped rows stay readable in the main file until it is overwritten.
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private static void Exec(System.Data.IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

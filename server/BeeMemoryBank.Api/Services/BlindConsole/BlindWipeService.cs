using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindConsole;

/// <summary>
/// "Disconnect and wipe" (plan §9): the blind node leaves the mesh and everything it stored for
/// it is destroyed locally — the vault tables, media files, blob rows, and the blind node's own
/// backup settings (including the locally-held restic password). The restic REPOSITORY is not
/// touched, wherever it lives: it may be a mounted folder owned by the operator or an S3 bucket
/// the node merely writes to, and destroying the last backup as part of "decommission this node"
/// is not a side effect anyone expects from that button.
///
/// <para>The wipe clears tables in place instead of deleting the database file (the Api holds an
/// open SQLite handle, and the point is a node that comes back empty, not a process that dies) —
/// the same shape as <see cref="NodeResetService"/> minus its master-password proof, which a
/// blind node cannot present. The caller (endpoint/CLI) owns the double confirmation: console
/// password re-entry plus typing the node's display name.</para>
/// </summary>
/// <summary>The wipe could not get the node quiet; nothing was deleted.</summary>
public sealed class BlindWipeRefusedException(string message) : InvalidOperationException(message);

/// <summary>The database could not be cleared completely; the transaction was rolled back.</summary>
public sealed class BlindWipeFailedException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class BlindWipeService(
    IDbConnectionFactory connFactory,
    INodeIdentityRepository nodeRepo,
    SessionService session,
    MaintenanceModeService maintenance,
    SyncTokenStore tokens,
    BlindJobManager jobs,
    BlindBackupSettingsStore settings,
    string dataPath,
    ILogger<BlindWipeService> logger)
{
    // restic gets SIGKILL on cancel and a 10 s bounded wait; a minute covers that with margin.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(60);

    public async Task WipeAsync(string initiatedBy, CancellationToken ct)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity == null)
            throw new InvalidOperationException("node is not initialized — nothing to wipe");

        // AUDIT: the wipe deletes tbl_audit_log along with everything else, so the trail must
        // live outside the database file (same reasoning as NodeResetService).
        var at = DateTime.UtcNow;
        try
        {
            File.AppendAllText(Path.Combine(dataPath, "wipe-audit.log"),
                $"{at:O} blind_wipe node_id={identity.NodeId} name=\"{identity.DisplayName}\" initiated_by={initiatedBy}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write wipe-audit.log (continuing with the wipe)");
        }
        logger.LogWarning("BLIND WIPE initiated: node {NodeId} \"{Name}\" by {By}", identity.NodeId, identity.DisplayName, initiatedBy);

        // Closed and drained before anything is touched; a job that will not stop fails the
        // wipe instead of racing it (it may still be reading the settings or the database).
        if (!await jobs.BeginWipeAsync(DrainTimeout))
            throw new BlindWipeRefusedException(
                $"a backup job did not stop within {DrainTimeout.TotalSeconds:0} s — nothing was wiped; try again");
        tokens.Clear();
        maintenance.Enter("Wiping the blind node…");
        session.Lock();
        try
        {
            WipeVaultDatabase();
            DeleteBlindFiles();
            FolderAccessService.InvalidateAll();
        }
        finally
        {
            maintenance.Exit();
            jobs.EndWipe();
        }
    }

    private void WipeVaultDatabase()
    {
        using var conn = connFactory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        Exec(conn, "PRAGMA foreign_keys = OFF");
        try
        {
            using (var tx = conn.BeginTransaction())
            {
                // Live schema, not a hand list — same reasoning as NodeResetService: a hand list
                // can omit a table a later migration adds, and the omitted table is exactly what
                // a wipe must not leave behind. The fts_ tables follow their base tables through
                // triggers.
                var tables = Query(conn, tx, @"
                    SELECT name FROM sqlite_master
                    WHERE type = 'table'
                      AND name NOT LIKE 'sqlite_%'
                      AND name NOT LIKE 'fts_%'
                      AND name <> 'tbl_migration'
                    ORDER BY name");

                // All or nothing: any table that refuses (a lock, a trigger, a constraint) aborts
                // the whole wipe and the transaction rolls back when it is disposed uncommitted.
                // A wipe that reports success over a table it could not clear is worse than one
                // that fails and says so.
                foreach (var table in tables)
                    Exec(conn, table == "tbl_role"
                        ? "DELETE FROM tbl_role WHERE is_system = 0"
                        : $"DELETE FROM [{table}]", tx);

                // Verified before it counts: every cleared table, and the full-text indexes of
                // the metadata (titles, paths, tags), must be empty.
                var fts = Query(conn, tx, @"
                    SELECT name FROM sqlite_master
                    WHERE type = 'table' AND name LIKE 'fts_%' AND sql LIKE 'CREATE VIRTUAL TABLE%'");
                foreach (var table in tables.Concat(fts))
                {
                    var where = table == "tbl_role" ? " WHERE is_system = 0" : "";
                    if (Count(conn, tx, $"SELECT COUNT(*) FROM [{table}]{where}") > 0)
                        throw new BlindWipeFailedException($"table {table} still holds rows after the wipe");
                }
                tx.Commit();
            }
        }
        catch (Exception ex) when (ex is not BlindWipeFailedException)
        {
            throw new BlindWipeFailedException($"the database could not be cleared ({ex.Message}); nothing was wiped", ex);
        }
        finally
        {
            Exec(conn, "PRAGMA foreign_keys = ON");
        }
        Exec(conn, "VACUUM");
    }

    private static List<string> Query(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static long Count(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void DeleteBlindFiles()
    {
        // Media ciphertext, then the blind node's own state. The restic CACHE is deleted but the
        // repository is deliberately absent from this list — see the class comment.
        TryDeleteFiles(Path.Combine(dataPath, "media"), "*.enc");
        var blindDir = Path.Combine(dataPath, "blind");
        using var closed = settings.CloseAndDelete();
        // Every temp file of an interrupted save (settings, job history, console): a torn
        // settings save leaves the restic and S3 secrets in settings.json.tmp.
        foreach (var name in new[] { "jobs.json", "cpu-mode" })
            TryDeleteOne(Path.Combine(blindDir, name));
        TryDeleteFiles(blindDir, "*.tmp");
        TryDeleteDirContents(Path.Combine(blindDir, "stage"));
        TryDeleteDirContents(Path.Combine(blindDir, "restic-cache"));

        // console.json survives on purpose: the console password is local to this box (never mesh
        // data) and keeping it means the operator who just wiped the node can still log in to
        // re-pair it instead of reaching for the CLI first.
    }

    private void TryDeleteFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, pattern))
            TryDeleteOne(f);
    }

    // Recursive: restic keeps its cache in <repo-id>/{index,snapshots,data}/ subdirectories, and a
    // top-level-only sweep leaves every one of them behind.
    private void TryDeleteDirContents(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
            TryDeleteOne(f);
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(sub, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Wipe: could not delete {Dir}", sub); }
        }
    }

    private void TryDeleteOne(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) { logger.LogWarning(ex, "Wipe: could not delete {File}", file); }
    }

    private static void Exec(System.Data.IDbConnection conn, string sql, System.Data.IDbTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

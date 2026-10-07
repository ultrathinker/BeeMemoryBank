using System.Data;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// The "a restore is halfway through its bootstrap" flag.
///
/// <para><see cref="InitializationService.IsInitializedAsync"/> answers the 1.0.11 question — is
/// there an identity row — because every other answer sends an operator whose vault is perfectly
/// intact to the setup wizard, and the rehearsal on a real 1.0.11 vault showed how real that is:
/// its identity row has a NULL sentinel, so any "the row is not enough" test reads a working vault
/// as an empty one (review release-a2 batch 2). The identity row alone is not enough in exactly one
/// situation, and it is a writer's own: the restore bootstrap writes that row first and the slot,
/// the admin and the sentinel after it, and a crash in between used to leave a node that called
/// itself initialized, refused the next restore with "restore needs a fresh node" and had no slot
/// to unlock with.</para>
///
/// <para>So the bootstrap raises this marker BEFORE its first write and lowers it in the
/// transaction that ends the whole restore: after the bootstrap (sentinel on disk, identity
/// verified), the peers with their final status, the clock floor and this device's recovery box
/// (review release-a2 A2-a). While it is up the node reports not-initialized, which is the
/// instruction the operator needs — restore again — and the next attempt is allowed in and resumes
/// instead of repeating (that is <see cref="RecoveryRestoreService"/>'s own re-entry rule). The
/// marker is not a security boundary: <see cref="InitializationService.IsClaimedAsync"/> is what
/// keeps a setup path away from a vault whose shape looks empty, whatever this flag says.</para>
///
/// <para>It lives in <c>tbl_migration_marker</c> — schema bookkeeping, carried in snapshots and
/// never imported over the local rows (see <c>SnapshotTables.SchemaMeta</c>) — so it survives the
/// restore's own import and every wipe that clears the vault tables.</para>
/// </summary>
public sealed class RestoreBootstrapMarker(IDbConnectionFactory dbFactory)
{
    /// <summary>The row this flag is: present = a bootstrap is in progress.</summary>
    public const string Key = "restore_bootstrap_in_progress";

    public async Task<bool> IsSetAsync(CancellationToken ct = default)
    {
        using var conn = dbFactory.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @k";
        AddParam(cmd, "k", Key);
        return Convert.ToInt64(await Task.Run(cmd.ExecuteScalar, ct)) > 0;
    }

    public async Task SetAsync(CancellationToken ct = default)
    {
        using var conn = dbFactory.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, '1', @ts)";
        AddParam(cmd, "k", Key);
        AddParam(cmd, "ts", DateTime.UtcNow.ToString("O"));
        await Task.Run(cmd.ExecuteNonQuery, ct);
    }

    /// <summary>
    /// Lowers the flag inside the caller's transaction: the last write of the restore and the flag
    /// that says it has not finished are one step, so a node can never report a finished restore
    /// over a half-written one, nor the other way round.
    /// </summary>
    public static void Clear(IDbConnection conn, IDbTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM tbl_migration_marker WHERE key = @k";
        AddParam(cmd, "k", Key);
        cmd.ExecuteNonQuery();
    }

    private static void AddParam(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
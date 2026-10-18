using System.Data;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The seal intent of this node's data folder (<see cref="SealIntent"/>), as the flows that set a node up use it: <see cref="Begin"/> before the first
/// thing they write in the open, <see cref="Complete"/> when the node is encrypted or was taken back to empty. A host that cannot encrypt a node
/// (the phone apps, a blind host) registers none, and the flows that take it as an optional dependency write no marker.
/// </summary>
public interface ISealIntents
{
    /// <summary>True while a setup, join or restore has begun and has neither ended encrypted nor been taken back.</summary>
    bool IsPending { get; }

    /// <summary>
    /// Writes the marker durably before the caller writes anything. Throws when it cannot: the flow must not start then. Also throws
    /// (<see cref="SealIntentException"/>) while another setup, join or restore runs or is pending, and when the target already holds a node.
    /// </summary>
    void Begin(string kind);

    /// <summary>The node is encrypted, or empty again. A marker that cannot be removed stays; the next start decides from the files.</summary>
    void Complete();
}

/// <inheritdoc cref="ISealIntents"/>
/// <param name="dataPath">The data folder of this node.</param>
/// <param name="targetIsEmpty">The check that the target holds no node, asked when an operation begins (see <see cref="HoldsNoNode"/>); null when the host cannot ask.</param>
public sealed class SealIntents(string dataPath, Func<bool>? targetIsEmpty = null) : ISealIntents
{
    public bool IsPending => SealIntent.IsPending(dataPath);

    public void Begin(string kind) => SealIntent.Begin(dataPath, kind, targetIsEmpty);

    public void Complete() => SealIntent.Complete(dataPath);

    /// <summary>
    /// True when the database holds no finished node: no identity row and no key slot (the two things <c>InitializationService</c> refuses to initialize over), or
    /// only what a restore that stopped halfway through its bootstrap left (<see cref="RestoreBootstrapMarker"/>: the node reports not-initialized, and the restore is
    /// run again - those rows are no node anybody keeps). A table that is not there yet is empty; anything else that goes wrong - an encrypted database that cannot be
    /// opened, a locked file - is "not empty": an operation never begins on a database that cannot be shown to be empty. A setup and a join keep their own, stricter
    /// guard (<c>IsClaimedAsync</c>) before they begin.
    /// </summary>
    public static bool HoldsNoNode(IDbConnectionFactory db)
    {
        try
        {
            using var conn = db.CreateConnection();
            if (conn.State != ConnectionState.Open) conn.Open();
            if (Count(conn, "tbl_node_identity", "") == 0 && Count(conn, "tbl_key_slot", "") == 0) return true;
            return Count(conn, "tbl_migration_marker", $" WHERE key = '{RestoreBootstrapMarker.Key}'") > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long Count(IDbConnection conn, string table, string where)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}{where}";
        try
        {
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
    }
}

using System.Data;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// The bridge from an old master DEK (release 2.12, plan WS-A3): the node-local chain material of a rotation (the NEW DEK wrapped under the
/// OLD one, <c>tbl_dek_rotation_state.chain_encrypted_new_dek</c>) is what lets a v1 door, which no rotation can re-seal, reach the current DEK
/// at its next use. It is also what lets the OLD DEK plus a later copy of the database reach the NEW one. So it is kept exactly as
/// long as a v1 door exists: a rotation writes it only then, and the change that removes the node's last v1 door (its upgrade or its delete)
/// clears every chain row in the same statement. The retired links (OLD under NEW) are never touched: late data and restores need them.
/// </summary>
public static class DekBridge
{
    /// <summary>Counts the v1 doors of the node (key slots; a v1 auto-unlock agent never survives a rotation).</summary>
    public const string CountV1DoorsSql = "SELECT COUNT(*) FROM tbl_key_slot WHERE door_version = 1";

    /// <summary>Clears every chain row, but only when no v1 door is left. One statement: the check and the clear cannot disagree.</summary>
    public const string EndIfUnusedSql =
        @"UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = NULL, chain_iv = NULL
           WHERE (chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL)
             AND NOT EXISTS (SELECT 1 FROM tbl_key_slot WHERE door_version = 1)";

    /// <summary>Clears every chain row unconditionally (the owner removes the bridge, together with the v1 doors).</summary>
    public const string ClearAllSql =
        @"UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = NULL, chain_iv = NULL
           WHERE chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL";

    /// <summary>Ends the bridge when the node has no v1 door left; returns the number of chain rows cleared. Tolerates a database before migration 046.</summary>
    public static int EndIfUnused(IDbConnection connection, IDbTransaction? transaction = null)
    {
        if (!HasDoorVersion(connection, transaction)) return 0;
        return connection.Execute(EndIfUnusedSql, transaction: transaction);
    }

    /// <summary>True when the node has at least one v1 door.</summary>
    public static bool HasV1Doors(IDbConnection connection, IDbTransaction? transaction = null) =>
        !HasDoorVersion(connection, transaction) || connection.ExecuteScalar<long>(CountV1DoorsSql, transaction: transaction) > 0;

    private static bool HasDoorVersion(IDbConnection connection, IDbTransaction? transaction) =>
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('tbl_key_slot') WHERE name = 'door_version'", transaction: transaction) > 0;
}

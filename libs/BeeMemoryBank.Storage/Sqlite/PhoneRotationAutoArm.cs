using System.Data;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// The phone follows every superadmin peer's DEK rotation unattended (it has no admin screen to
/// approve one; BMB-31, scenario 20). A blind node is never one of those peers: it holds no DEK and
/// must never be trusted to rewrap the phone's vault, even if an older row marks it superadmin.
/// </summary>
public static class PhoneRotationAutoArm
{
    public static Task<int> ArmAsync(IDbConnection conn) => conn.ExecuteAsync(
        @"UPDATE tbl_whitelist SET auto_accept_dek_rotation = 1
          WHERE is_superadmin = 1 AND auto_accept_dek_rotation = 0
            AND NOT (lower(substr(node_id, 1, 4)) = 'b11d'
                     AND substr(node_id, 15, 1) = '8'
                     AND lower(substr(node_id, 20, 1)) IN ('8', '9', 'a', 'b')
                     AND length(node_id) = 36)");
}

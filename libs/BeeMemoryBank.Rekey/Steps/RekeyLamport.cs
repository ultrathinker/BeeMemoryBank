using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// The highest Lamport timestamp anywhere in the copy: every <c>lamport_ts</c> and <c>delete_lamport_ts</c> column of
/// every table, and the event log. The node seeds its clock from the event log alone at start-up, so once the log is
/// cleared the fresh checkpoint must carry a value above all of these. Otherwise the node's next writes would sort
/// before rows it already holds, and a peer would keep the older row.
/// </summary>
internal static class RekeyLamport
{
    public static async Task<long> MaxAsync(SqliteConnection db, SqliteTransaction? tx = null)
    {
        long max = 0;
        var tables = (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", transaction: tx)).ToList();
        foreach (var table in tables)
        {
            var columns = (await db.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')", transaction: tx))
                .Where(c => c is "lamport_ts" or "delete_lamport_ts").ToList();
            foreach (var column in columns)
                max = Math.Max(max, await db.ExecuteScalarAsync<long?>($"SELECT MAX({column}) FROM [{table}]", transaction: tx) ?? 0);
        }
        return max;
    }
}

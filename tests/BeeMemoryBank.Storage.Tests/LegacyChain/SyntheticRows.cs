using System.Data;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// A few synthetic rows in every ordinary table of a database, whatever its shape (the databases of the released versions differ):
/// the values follow each column's declared type, a column with a default keeps it, and what a constraint or a trigger refuses is
/// retried with the bare minimum (the not-null columns that have no default) and then given up on. Foreign keys are off while it
/// runs: the point is rows to carry through an adoption, not a consistent vault.
/// </summary>
internal static class SyntheticRows
{
    public sealed record Column(string Name, string Type, bool NotNull, string? Default, bool IsPk);

    /// <returns>Rows inserted per table.</returns>
    public static Dictionary<string, int> Seed(IDbConnection conn, int rowsPerTable = 3)
    {
        conn.Execute("PRAGMA foreign_keys = OFF");
        var inserted = new Dictionary<string, int>();
        foreach (var table in SchemaDump.RowTables(conn))
        {
            var columns = conn.Query<(string Name, string Type, long NotNull, string? Default, long Pk, long Hidden)>(
                $"SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('{table}') WHERE hidden = 0")
                .Select(c => new Column(c.Name, c.Type, c.NotNull == 1, c.Default, c.Pk > 0)).ToList();
            var count = 0;
            for (var i = 1; i <= rowsPerTable; i++)
                if (TryInsert(conn, table, columns, i, full: true) || TryInsert(conn, table, columns, i, full: false)) count++;
            inserted[table] = count;
        }
        conn.Execute("PRAGMA foreign_keys = ON");
        return inserted;
    }

    private static bool TryInsert(IDbConnection conn, string table, List<Column> columns, int i, bool full)
    {
        var names = new List<string>();
        var parameters = new DynamicParameters();
        foreach (var c in columns)
        {
            var single = columns.Count(x => x.IsPk) == 1;
            var type = c.Type.ToUpperInvariant();
            // The rowid alias of the table (a lone INTEGER PRIMARY KEY) is left to SQLite.
            if (c.IsPk && single && type == "INTEGER") continue;
            // A column that has a default and is not part of a key keeps it; so does any column that may be NULL in the minimal form.
            if (c.Default is not null && !c.IsPk) continue;
            if (!full && !c.NotNull && !c.IsPk) continue;
            names.Add(c.Name);
            parameters.Add("p" + (names.Count - 1), ValueFor(table, c, type, i));
        }
        try
        {
            var sql = names.Count == 0
                ? $"INSERT INTO \"{table}\" DEFAULT VALUES"
                : $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, k) => "@p" + k))})";
            conn.Execute(sql, parameters);
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    private static object ValueFor(string table, Column c, string type, int i)
    {
        if (type.Contains("INT")) return (long)i;
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB")) return i + 0.5;
        if (type.Contains("BLOB")) return new byte[] { (byte)i, 0x5a, (byte)(i * 3) };
        if (c.Name.EndsWith("_at", StringComparison.Ordinal)) return $"2026-10-0{i}T10:00:00.0000000Z";
        return $"{table}.{c.Name}.{i}";
    }
}

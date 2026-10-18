using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// Makes <c>libs/BeeMemoryBank.Storage/Migrations/040_baseline.sql</c> from the REAL final schema: a database is built by the old chain
/// (the frozen runner over the 39 files of 2.6.1) and every object is written out as <c>sqlite_master</c> holds it, then the rows the old
/// migrations seeded. Nothing is concatenated by hand. <c>BMB_UPDATE_BASELINE=1</c> rewrites the file; otherwise the test only checks that
/// the file is exactly what this makes.
/// </summary>
internal static class BaselineGenerator
{
    public const string Header = """
        -- 040_baseline.sql
        --
        -- The schema of Bee Memory Bank at migration 040, the last of the 39 migrations of 2.6.0 and 2.6.1. From 2.6.2 on this is the only
        -- migration file: a new database gets it once, a database that already ran the old 39 is adopted without running anything
        -- (MigrationRunner / LegacyChainAdoption). Migrations after it are numbered 041 and up.
        --
        -- GENERATED from a database built by the old chain (tests/BeeMemoryBank.Storage.Tests/LegacyChain/BaselineGenerator.cs;
        -- BMB_UPDATE_BASELINE=1 rewrites this file) and kept exactly that by BaselineTests. Do not edit by hand.
        -- Order: tables, the rows the old migrations seeded, full-text tables, indexes, views, triggers.

        """;

    private static readonly string[] ShadowSuffixes = ["_data", "_idx", "_docsize", "_config", "_content"];
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled);

    public static async Task<string> GenerateAsync(string workDir)
    {
        var path = Path.Combine(workDir, "baseline-source.db");
        using (var built = await LegacyChain.BuildAsync(path, "v2.6.1")) { }
        SqliteConnection.ClearAllPools();
        string text;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            text = Dump(conn);
        SqliteConnection.ClearAllPools();
        return text;
    }

    public static string Dump(IDbConnection conn)
    {
        var objects = conn.Query<(string Type, string Name, string TblName, string Sql)>(
            @"SELECT type, name, tbl_name, sql FROM sqlite_master
              WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite\_%' ESCAPE '\' AND tbl_name <> 'tbl_migration' ORDER BY rowid").ToList();
        var virtuals = objects.Where(o => o.Type == "table" && o.Sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)).Select(o => o.Name).ToList();
        bool IsShadow(string name) => virtuals.Any(v => ShadowSuffixes.Any(s => name == v + s));

        var sb = new StringBuilder(Header).Append('\n');
        void Write(string kind, string name, string sql) => sb.Append("-- ").Append(kind).Append(' ').Append(name).Append('\n').Append(sql.Trim()).Append(";\n\n");

        var tables = objects.Where(o => o.Type == "table" && !virtuals.Contains(o.Name) && !IsShadow(o.Name)).ToList();
        foreach (var t in tables) Write("table", t.Name, t.Sql);
        foreach (var t in tables) SeedRows(conn, t.Name, t.Sql, sb);
        SequenceRows(conn, sb);
        foreach (var v in objects.Where(o => virtuals.Contains(o.Name))) Write("full-text table", v.Name, v.Sql);
        foreach (var i in objects.Where(o => o.Type == "index")) Write("index", i.Name, i.Sql);
        foreach (var v in objects.Where(o => o.Type == "view")) Write("view", v.Name, v.Sql);
        foreach (var t in objects.Where(o => o.Type == "trigger")) Write("trigger", t.Name, t.Sql);
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// The AUTOINCREMENT counters the old chain left that its seed rows do not create by themselves (a table that was rebuilt by a migration keeps
    /// an entry at 0). Written so that an entry that is already there is left alone.
    /// </summary>
    private static void SequenceRows(IDbConnection conn, StringBuilder sb)
    {
        var any = false;
        foreach (var (name, seq) in conn.Query<(string Name, long Seq)>("SELECT name, seq FROM sqlite_sequence WHERE name <> 'tbl_migration' ORDER BY rowid"))
        {
            if (!any) sb.Append("-- AUTOINCREMENT counters the old migrations left\n");
            any = true;
            sb.Append("INSERT INTO sqlite_sequence (name, seq) SELECT '").Append(name).Append("', ").Append(seq.ToString(CultureInfo.InvariantCulture))
              .Append(" WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = '").Append(name).Append("');\n");
        }
        if (any) sb.Append('\n');
    }

    private static void SeedRows(IDbConnection conn, string table, string tableSql, StringBuilder sb)
    {
        // Rows in a fixed order: by rowid, or by the primary key for a WITHOUT ROWID table.
        var order = tableSql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)
            ? string.Join(", ", conn.Query<string>($"SELECT '\"' || name || '\"' FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk"))
            : "rowid";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {order}";
        using var reader = cmd.ExecuteReader();
        var any = false;
        while (reader.Read())
        {
            if (!any) sb.Append("-- rows seeded by the old migrations: ").Append(table).Append('\n');
            any = true;
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => Literal(columns[i], reader.GetValue(i))).ToList();
            sb.Append("INSERT INTO \"").Append(table).Append("\" (").Append(string.Join(", ", columns.Select(c => $"\"{c}\""))).Append(") VALUES (")
              .Append(string.Join(", ", values)).Append(");\n");
        }
        if (any) sb.Append('\n');
    }

    private static string Literal(string column, object value) => value switch
    {
        DBNull => "NULL",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] b => "X'" + Convert.ToHexString(b) + "'",
        string s when column.EndsWith("_at", StringComparison.Ordinal) && Timestamp.IsMatch(s) =>
            // The time a migration ran is the time the baseline runs: a seeded row is stamped when it is written, as the old migration did it.
            s.Contains('.') ? "strftime('%Y-%m-%dT%H:%M:%fZ', 'now')" : "strftime('%Y-%m-%dT%H:%M:%SZ', 'now')",
        string s => "'" + s.Replace("'", "''") + "'",
        _ => throw new NotSupportedException(value.GetType().Name),
    };
}

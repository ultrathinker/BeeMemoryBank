using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// The oracle of the squash tests: one deterministic text that says everything about the SHAPE of a database (tables with their columns,
/// types, not-null, defaults and primary keys, foreign keys, indexes with their columns, triggers, views, FTS definitions and shadow tables,
/// the AUTOINCREMENT counters) and, separately, a digest of the ROWS of every ordinary table. Written against SQLite's pragmas and
/// <c>sqlite_master</c> on purpose, not against the product's own schema reader, so the test does not trust what it tests.
/// </summary>
internal static class SchemaDump
{
    private static readonly Regex Blanks = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Timestamp = new(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?Z?", RegexOptions.Compiled);

    public static string Normalize(string? sql) => sql is null ? "" : Blanks.Replace(sql, " ").Trim();

    /// <param name="withLedger">Whether the tbl_migration table's own definition is part of the text (its rows never are).</param>
    public static string Shape(IDbConnection conn, bool withLedger = true, bool withSequences = true)
    {
        var sb = new StringBuilder();
        var objects = conn.Query<(string Type, string Name, string TblName, string? Sql)>(
            "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name").ToList();
        foreach (var (type, name, tblName, sql) in objects)
        {
            if (!withLedger && (name == "tbl_migration" || tblName == "tbl_migration")) continue;
            if (type == "table")
            {
                sb.Append("TABLE ").Append(name).Append(" | ").AppendLine(sql is not null && sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) ? Normalize(sql) : "");
                foreach (var c in conn.Query<(long Cid, string Name, string Type, long NotNull, string? Default, long Pk, long Hidden)>(
                    $"SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('{name.Replace("'", "''")}')"))
                    sb.Append("  COL ").Append(c.Cid).Append(' ').Append(c.Name).Append(" type=").Append(c.Type.ToUpperInvariant())
                      .Append(" notnull=").Append(c.NotNull).Append(" default=").Append(c.Default ?? "<none>")
                      .Append(" pk=").Append(c.Pk).Append(" hidden=").AppendLine(c.Hidden.ToString());
                foreach (var f in conn.Query<(long Id, long Seq, string Table, string From, string? To, string OnUpdate, string OnDelete, string Match)>(
                    $"SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, \"match\" FROM pragma_foreign_key_list('{name.Replace("'", "''")}') ORDER BY id, seq"))
                    sb.Append("  FK ").Append(f.Id).Append('.').Append(f.Seq).Append(' ').Append(f.From).Append(" -> ").Append(f.Table).Append('(').Append(f.To).Append(") upd=")
                      .Append(f.OnUpdate).Append(" del=").Append(f.OnDelete).Append(" match=").AppendLine(f.Match);
                foreach (var ix in conn.Query<(string Name, long Unique, string Origin, long Partial)>(
                    $"SELECT name, \"unique\", origin, partial FROM pragma_index_list('{name.Replace("'", "''")}')").OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    // An auto-index (UNIQUE or PRIMARY KEY inside CREATE TABLE) carries a name SQLite made up; its columns say what it is.
                    var label = ix.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal) ? "<auto>" : ix.Name;
                    var cols = string.Join(",", conn.Query<(long Seq, long Cid, string? Name, long Desc, string Coll, long Key)>(
                        $"SELECT seqno, cid, name, \"desc\", coll, \"key\" FROM pragma_index_xinfo('{ix.Name.Replace("'", "''")}') ORDER BY seqno")
                        .Where(k => k.Key == 1).Select(k => $"{k.Name}/{k.Desc}/{k.Coll}"));
                    sb.Append("  IDX ").Append(label).Append(" unique=").Append(ix.Unique).Append(" origin=").Append(ix.Origin)
                      .Append(" partial=").Append(ix.Partial).Append(" cols=").AppendLine(cols);
                }
            }
            else
                sb.Append(type.ToUpperInvariant()).Append(' ').Append(name).Append(" ON ").Append(tblName).Append(" | ").AppendLine(Normalize(sql));
        }
        if (withSequences)
            foreach (var s in conn.Query<(string Name, long Seq)>("SELECT name, seq FROM sqlite_sequence WHERE name <> 'tbl_migration' ORDER BY name"))
                sb.Append("SEQ ").Append(s.Name).Append(' ').AppendLine(s.Seq.ToString());
        return sb.ToString();
    }

    /// <summary>The names of the ordinary tables whose rows <see cref="Rows"/> digests (no ledger, no FTS shadow tables, no SQLite tables).</summary>
    public static List<string> RowTables(IDbConnection conn) =>
        conn.Query<string>(
            @"SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'fts\_%' ESCAPE '\'
                AND name <> 'tbl_migration' AND (sql IS NULL OR sql NOT LIKE 'CREATE VIRTUAL%') ORDER BY name").ToList();

    /// <summary>Per table: the row count and a digest of every row (a row is its values, a blob as hex; rows sorted as text).</summary>
    public static SortedDictionary<string, (long Count, string Digest)> Rows(IDbConnection conn, bool maskTimestamps = false)
    {
        var result = new SortedDictionary<string, (long, string)>(StringComparer.Ordinal);
        foreach (var table in RowTables(conn))
        {
            var lines = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\"";
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    var cells = new string[reader.FieldCount];
                    for (var i = 0; i < cells.Length; i++)
                    {
                        var v = reader.GetValue(i);
                        cells[i] = v switch { DBNull => "<null>", byte[] b => Convert.ToHexString(b), _ => $"{v.GetType().Name}:{v}" };
                        if (maskTimestamps) cells[i] = Timestamp.Replace(cells[i], "<ts>");
                    }
                    lines.Add(string.Join("\u001f", cells));
                }
            lines.Sort(StringComparer.Ordinal);
            result[table] = (lines.Count, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))));
        }
        return result;
    }

    public static string RowsText(IDbConnection conn, bool maskTimestamps = false) =>
        string.Join("\n", Rows(conn, maskTimestamps).Select(r => $"{r.Key} {r.Value.Count} {r.Value.Digest}"));

    public static List<(int Version, string Filename)> Ledger(IDbConnection conn) =>
        conn.Query<(int, string)>("SELECT version, filename FROM tbl_migration ORDER BY version").ToList();

    public static string FileDigest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

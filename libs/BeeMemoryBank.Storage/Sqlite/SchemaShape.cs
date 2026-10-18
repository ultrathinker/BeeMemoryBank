using System.Data;
using System.Text.RegularExpressions;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// What a database is made of, read from <c>sqlite_master</c> and the table pragmas: tables with their columns, foreign keys and unique
/// constraints, explicit indexes, views, triggers, and the full-text tables with their shadow tables. <see cref="Compare"/> sets the shape of
/// a database against the shape of the baseline and says what is missing and can be created, what differs and has to be replaced, what is
/// wrong beyond repair, and what is merely extra. Used by <see cref="LegacyChainAdoption"/> and the restore preflight; it reads, it never writes.
///
/// <para>A table's shape is its meaning, not its text: the columns (type affinity, NOT NULL, DEFAULT, collation, generated or not, primary
/// key), the CHECK constraints, WITHOUT ROWID, STRICT, AUTOINCREMENT, the foreign keys and the unique rules. What the pragmas do not tell
/// (collation, CHECK, generated expression, the table options) is read from the table's CREATE statement, tokenized, so that spacing, case,
/// comments, redundant parentheses and the place a column sits in (an ALTER TABLE ADD COLUMN leaves it at the end) make no difference.</para>
/// </summary>
internal sealed class SchemaShape
{
    /// <param name="Hidden">0 an ordinary column, 2 a virtual generated column, 3 a stored generated column (1 is a hidden column of a virtual table).</param>
    /// <param name="Collation">Upper case; BINARY when none is declared.</param>
    /// <param name="Generated">Kind and normalized expression of a generated column, otherwise null.</param>
    public sealed record Column(string Name, string Type, bool NotNull, string? Default, int Pk, int Hidden = 0, string Collation = "BINARY", string? Generated = null);

    public sealed class Table
    {
        public required string Name { get; init; }
        public required string Sql { get; init; }
        public bool IsVirtual { get; init; }
        public List<Column> Columns { get; } = [];
        public HashSet<string> ForeignKeys { get; } = [];
        public HashSet<string> Uniques { get; } = [];

        /// <summary>Normalized CHECK expressions of the table, those of its columns included (SQLite treats them alike).</summary>
        public HashSet<string> Checks { get; } = [];
        public bool WithoutRowId { get; set; }
        public bool Strict { get; set; }
        public bool Autoincrement { get; set; }

        /// <summary>The CREATE statement could not be read (not a statement of the shape SQLite writes); what it would have told is unknown.</summary>
        public bool Unparsed { get; set; }
    }

    public sealed record Named(string Table, string Sql);

    public Dictionary<string, Table> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Named> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Named> Views { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Named> Triggers { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ShadowSuffixes = ["_data", "_idx", "_docsize", "_config", "_content"];
    private const string Ledger = "tbl_migration";

    public static SchemaShape Read(IDbConnection conn, IDbTransaction? tx = null)
    {
        var shape = new SchemaShape();
        var objects = conn.Query<(string Type, string Name, string TblName, string? Sql)>(
            "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY rowid", transaction: tx).ToList();
        foreach (var (type, name, tblName, sql) in objects)
        {
            if (sql is null) continue; // an automatic index: its columns are read with the table
            switch (type)
            {
                case "table":
                    shape.Tables[name] = ReadTable(conn, tx, name, sql);
                    break;
                case "index": shape.Indexes[name] = new Named(tblName, sql); break;
                case "view": shape.Views[name] = new Named(tblName, sql); break;
                case "trigger": shape.Triggers[name] = new Named(tblName, sql); break;
            }
        }
        return shape;
    }

    private static Table ReadTable(IDbConnection conn, IDbTransaction? tx, string name, string sql)
    {
        var table = new Table { Name = name, Sql = sql, IsVirtual = sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) };
        var literal = name.Replace("'", "''");
        var columns = conn.Query<(string Name, string Type, long NotNull, string? Default, long Pk, long Hidden)>(
            $"SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('{literal}')", transaction: tx).ToList();
        if (table.IsVirtual)
        {
            foreach (var c in columns.Where(c => c.Hidden == 0))
                table.Columns.Add(new Column(c.Name, c.Type, c.NotNull == 1, c.Default, (int)c.Pk));
            return table;
        }

        // What the pragmas do not tell comes from the statement itself: the collation of a column, the CHECKs, the generated expressions, the options.
        var definition = TableDefinition.Parse(sql);
        table.Unparsed = definition is null;
        if (definition is not null)
        {
            table.WithoutRowId = definition.WithoutRowId;
            table.Strict = definition.Strict;
            table.Autoincrement = definition.Autoincrement;
            table.Checks.UnionWith(definition.Checks);
        }
        foreach (var c in columns)
            table.Columns.Add(new Column(c.Name, c.Type, c.NotNull == 1, c.Default, (int)c.Pk, (int)c.Hidden,
                definition?.Collations.GetValueOrDefault(c.Name) ?? "BINARY", definition?.Generated.GetValueOrDefault(c.Name)));

        foreach (var fk in conn.Query<(long Id, string Table, string From, string? To, string OnUpdate, string OnDelete)>(
            $"SELECT id, \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list('{literal}') ORDER BY id, seq", transaction: tx)
            .GroupBy(f => f.Id))
        {
            var first = fk.First();
            table.ForeignKeys.Add($"{string.Join(",", fk.Select(f => f.From))}>{first.Table}({string.Join(",", fk.Select(f => f.To))})|{first.OnUpdate}|{first.OnDelete}");
        }
        foreach (var ix in conn.Query<(string Name, string Origin)>(
            $"SELECT name, origin FROM pragma_index_list('{literal}') WHERE origin IN ('u', 'pk')", transaction: tx))
        {
            var cols = conn.Query<(string Name, string Collation)>(
                $"SELECT name, coll FROM pragma_index_xinfo('{ix.Name.Replace("'", "''")}') WHERE key = 1 ORDER BY seqno", transaction: tx)
                .Select(c => c.Collation.Equals("BINARY", StringComparison.OrdinalIgnoreCase) ? c.Name : $"{c.Name} COLLATE {c.Collation.ToUpperInvariant()}");
            table.Uniques.Add($"{ix.Origin}:{string.Join(",", cols)}");
        }
        return table;
    }

    // ---- comparison --------------------------------------------------------------------------------------------------------------

    public sealed class Diff
    {
        /// <summary>What is wrong beyond repair: the database must be refused.</summary>
        public List<string> Problems { get; } = [];

        /// <summary>Statements that create what is missing or replace what differs (tables, then indexes, views, triggers).</summary>
        public List<string> Repairs { get; } = [];

        /// <summary>Objects the database has that the baseline does not (left alone).</summary>
        public List<string> Extras { get; } = [];

        /// <summary>Differences that need no repair and no refusal: a spelling of a type, a relaxed NOT NULL, a default.</summary>
        public List<string> Tolerated { get; } = [];

        /// <summary>The names of the objects the repairs create or replace, for the log.</summary>
        public List<string> Repaired { get; } = [];
    }

    private static readonly Regex Blanks = new(@"\s+", RegexOptions.Compiled);
    private static string Normalize(string sql) => Blanks.Replace(sql, " ").Trim();
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>SQLite's rules for the affinity of a declared type: what a column stores does not depend on how the type is spelled.</summary>
    private static string Affinity(string declared)
    {
        var t = declared.ToUpperInvariant();
        if (t.Contains("INT")) return "INTEGER";
        if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT")) return "TEXT";
        if (t.Length == 0 || t.Contains("BLOB")) return "BLOB";
        if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB")) return "REAL";
        return "NUMERIC";
    }

    private static bool IsShadowOf(string name, IEnumerable<Table> virtuals) =>
        virtuals.Any(v => ShadowSuffixes.Any(s => name.Equals(v.Name + s, StringComparison.OrdinalIgnoreCase)));

    public static Diff Compare(SchemaShape reference, SchemaShape actual)
    {
        var diff = new Diff();
        var refVirtuals = reference.Tables.Values.Where(t => t.IsVirtual).ToList();

        // Tables. A missing one is created; a column that is missing or cannot take what the program writes is beyond repair.
        foreach (var r in reference.Tables.Values)
        {
            if (!r.IsVirtual && IsShadowOf(r.Name, refVirtuals))
            {
                // Created together with its full-text table; alone it is part of a broken index.
                if (!actual.Tables.ContainsKey(r.Name) && actual.Tables.TryGetValue(refVirtuals.First(v => IsShadowOf(r.Name, [v])).Name, out _))
                    diff.Problems.Add($"the search index table {r.Name} is missing");
                continue;
            }
            if (!actual.Tables.TryGetValue(r.Name, out var a))
            {
                diff.Repairs.Add(r.Sql);
                if (r.IsVirtual) diff.Repairs.Add($"INSERT INTO {Quote(r.Name)}({Quote(r.Name)}) VALUES ('rebuild')");
                diff.Repaired.Add(r.Name);
                continue;
            }
            CompareTable(r, a, diff);
        }

        // Indexes, views, triggers: a missing one is created, one that differs is replaced (they hold no data of their own).
        CompareNamed("index", "INDEX", reference.Indexes, actual.Indexes, diff);
        CompareNamed("view", "VIEW", reference.Views, actual.Views, diff);
        CompareNamed("trigger", "TRIGGER", reference.Triggers, actual.Triggers, diff);

        foreach (var name in actual.Tables.Keys.Where(n => n != Ledger && !reference.Tables.ContainsKey(n) && !IsShadowOf(n, refVirtuals)))
            diff.Extras.Add(name);
        foreach (var (name, n) in actual.Indexes.Where(i => i.Value.Table != Ledger && !reference.Indexes.ContainsKey(i.Key))) diff.Extras.Add(name);
        foreach (var (name, n) in actual.Views.Where(i => !reference.Views.ContainsKey(i.Key))) diff.Extras.Add(name);
        foreach (var (name, n) in actual.Triggers.Where(i => i.Value.Table != Ledger && !reference.Triggers.ContainsKey(i.Key))) diff.Extras.Add(name);
        return diff;
    }

    private static void CompareTable(Table r, Table a, Diff diff)
    {
        if (r.IsVirtual != a.IsVirtual)
        {
            diff.Problems.Add($"{r.Name} is not the kind of table the program expects");
            return;
        }
        if (r.IsVirtual)
        {
            if (Normalize(r.Sql) != Normalize(a.Sql)) diff.Problems.Add($"the search index {r.Name} is defined differently");
            return;
        }

        foreach (var rc in r.Columns)
        {
            var ac = a.Columns.FirstOrDefault(c => c.Name.Equals(rc.Name, StringComparison.OrdinalIgnoreCase));
            if (ac is null) { diff.Problems.Add($"the column {r.Name}.{rc.Name} is missing"); continue; }
            var (refDefault, actDefault) = (NormalizeDefault(rc.Default), NormalizeDefault(ac.Default));
            if (ac.Hidden != rc.Hidden)
                diff.Problems.Add(rc.Hidden >= 2 && ac.Hidden >= 2 ? $"the column {r.Name}.{rc.Name} is generated the other way (stored or virtual) than the program expects"
                    : ac.Hidden >= 2 ? $"the column {r.Name}.{rc.Name} is generated where the program writes it"
                    : $"the column {r.Name}.{rc.Name} is not generated as the program expects");
            else if (rc.Hidden >= 2 && ac.Generated != rc.Generated && !a.Unparsed)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} is generated from another expression than the program expects");
            else if (Affinity(ac.Type) != Affinity(rc.Type))
                diff.Problems.Add($"the column {r.Name}.{rc.Name} has the type {ac.Type.ToUpperInvariant()} where {rc.Type.ToUpperInvariant()} is expected");
            else if (ac.Pk != rc.Pk)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} is not part of the primary key as expected");
            else if (ac.NotNull && !rc.NotNull)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} is NOT NULL where the program writes NULL");
            else if (refDefault is not null && actDefault is null)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} has no default value where the program relies on one");
            else if (refDefault is null && actDefault is not null)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} has a default value where the program expects none");
            else if (refDefault != actDefault)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} has another default value than the program relies on");
            else if (!a.Unparsed && ac.Collation != rc.Collation)
                diff.Problems.Add($"the column {r.Name}.{rc.Name} compares with the collation {ac.Collation} where {rc.Collation} is expected");
            else if (!ac.Type.Equals(rc.Type, StringComparison.OrdinalIgnoreCase) || ac.NotNull != rc.NotNull || ac.Default != rc.Default)
                diff.Tolerated.Add($"{r.Name}.{rc.Name}");
        }

        // A column the program does not know is harmless, unless an INSERT of the program would fail on it.
        foreach (var ac in a.Columns.Where(ac => ac.NotNull && ac.Pk == 0 && ac.Hidden < 2 && NormalizeDefault(ac.Default) is null
                                                 && !r.Columns.Any(rc => rc.Name.Equals(ac.Name, StringComparison.OrdinalIgnoreCase))))
            diff.Problems.Add($"the column {r.Name}.{ac.Name} is NOT NULL and has no default value, and the program does not write it");

        if (a.Unparsed)
            diff.Problems.Add($"the definition of {r.Name} could not be read");
        else
        {
            if (r.WithoutRowId != a.WithoutRowId)
                diff.Problems.Add(a.WithoutRowId ? $"{r.Name} is a WITHOUT ROWID table where the program expects an ordinary one" : $"{r.Name} is an ordinary table where the program expects WITHOUT ROWID");
            if (r.Strict != a.Strict)
                diff.Problems.Add(a.Strict ? $"{r.Name} is a STRICT table where the program expects an ordinary one" : $"{r.Name} is not a STRICT table as the program expects");
            if (r.Autoincrement != a.Autoincrement)
                diff.Problems.Add(a.Autoincrement ? $"{r.Name} has AUTOINCREMENT where the program expects none" : $"{r.Name} has lost its AUTOINCREMENT");
            foreach (var check in r.Checks.Where(c => !a.Checks.Contains(c)))
                diff.Problems.Add($"a CHECK constraint of {r.Name} is missing or different ({check})");
            foreach (var check in a.Checks.Where(c => !r.Checks.Contains(c)))
                diff.Problems.Add($"{r.Name} has a CHECK constraint the program does not expect ({check})");
        }
        foreach (var fk in r.ForeignKeys.Where(f => !a.ForeignKeys.Contains(f)))
            diff.Problems.Add($"a foreign key of {r.Name} is missing ({fk.Split('|')[0]})");
        foreach (var u in r.Uniques.Where(u => !a.Uniques.Contains(u)))
            diff.Problems.Add($"a uniqueness rule of {r.Name} is missing ({u})");
    }

    // ---- the text of a table's definition, as tokens ---------------------------------------------------------------------------------

    private enum Kind { Word, Literal, Number, Punct }

    /// <summary>A word (identifiers unquoted and upper-cased: SQLite does not tell them apart by case), a string literal (kept as written), a number, one punctuation mark.</summary>
    private readonly record struct Token(Kind Kind, string Text)
    {
        public bool Is(string word) => Kind == Kind.Word && Text == word;
        public bool IsPunct(char c) => Kind == Kind.Punct && Text.Length == 1 && Text[0] == c;
    }

    /// <summary>Null when the text is not made of tokens (an unterminated literal or quoted name).</summary>
    private static List<Token>? Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
                continue;
            }
            if (c is '\'' or '"' or '`' or '[')
            {
                var closer = c == '[' ? ']' : c;
                var j = i + 1;
                var inner = new System.Text.StringBuilder();
                while (true)
                {
                    if (j >= sql.Length) return null;
                    if (sql[j] == closer)
                    {
                        if (closer != ']' && j + 1 < sql.Length && sql[j + 1] == closer) { inner.Append(closer); j += 2; continue; }
                        break;
                    }
                    inner.Append(sql[j++]);
                }
                tokens.Add(c == '\'' ? new Token(Kind.Literal, sql[i..(j + 1)]) : new Token(Kind.Word, inner.ToString().ToUpperInvariant()));
                i = j + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] is '_' or '$')) j++;
                tokens.Add(new Token(Kind.Word, sql[i..j].ToUpperInvariant()));
                i = j;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                var j = i;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '.'
                       || (sql[j] is '+' or '-' && sql[j - 1] is 'e' or 'E' && !sql[i..j].StartsWith("0x", StringComparison.OrdinalIgnoreCase)))) j++;
                tokens.Add(new Token(Kind.Number, sql[i..j]));
                i = j;
                continue;
            }
            var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
            if (two is "<=" or ">=" or "<>" or "!=" or "==" or "||" or "<<" or ">>") { tokens.Add(new Token(Kind.Punct, two)); i += 2; continue; }
            tokens.Add(new Token(Kind.Punct, c.ToString()));
            i++;
        }
        return tokens;
    }

    /// <summary>The index of the parenthesis that closes the one at <paramref name="open"/>, or -1.</summary>
    private static int Closing(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var j = open; j < tokens.Count; j++)
        {
            if (tokens[j].IsPunct('(')) depth++;
            else if (tokens[j].IsPunct(')') && --depth == 0) return j;
        }
        return -1;
    }

    /// <summary>An expression as one string: tokens joined by single blanks, redundant outer parentheses taken off.</summary>
    private static string Expression(List<Token> tokens)
    {
        var from = 0;
        var to = tokens.Count; // exclusive
        while (to - from >= 2 && tokens[from].IsPunct('(') && Closing(tokens, from) == to - 1) { from++; to--; }
        return string.Join(" ", tokens.Skip(from).Take(to - from).Select(t => t.Text));
    }

    /// <summary>A DEFAULT as the pragma reports it, in the form <see cref="Expression"/> gives; NULL is no default.</summary>
    private static string? NormalizeDefault(string? raw)
    {
        if (raw is null) return null;
        var tokens = Tokenize(raw);
        if (tokens is null) return raw.Trim();
        var text = Expression(tokens);
        return text == "NULL" ? null : text;
    }

    /// <summary>What a CREATE TABLE statement says beyond what the pragmas report.</summary>
    private sealed class TableDefinition
    {
        public bool WithoutRowId { get; private set; }
        public bool Strict { get; private set; }
        public bool Autoincrement { get; private set; }
        public HashSet<string> Checks { get; } = [];
        public Dictionary<string, string> Collations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Generated { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static TableDefinition? Parse(string sql)
        {
            var tokens = Tokenize(sql);
            if (tokens is null || tokens.Count == 0 || !tokens[0].Is("CREATE")) return null;
            var open = tokens.FindIndex(t => t.IsPunct('('));
            var close = open < 0 ? -1 : Closing(tokens, open);
            if (close < 0) return null;

            var definition = new TableDefinition();
            for (var j = close + 1; j < tokens.Count; j++)
            {
                if (tokens[j].Is("WITHOUT") && j + 1 < tokens.Count && tokens[j + 1].Is("ROWID")) definition.WithoutRowId = true;
                else if (tokens[j].Is("STRICT")) definition.Strict = true;
            }

            var item = new List<Token>();
            var depth = 0;
            for (var j = open + 1; j < close; j++)
            {
                var t = tokens[j];
                if (depth == 0 && t.IsPunct(',')) { definition.ReadItem(item); item = []; continue; }
                if (t.IsPunct('(')) depth++;
                else if (t.IsPunct(')')) depth--;
                item.Add(t);
            }
            definition.ReadItem(item);
            return definition;
        }

        /// <summary>One comma-separated part of the table body: a column with its constraints, or a table constraint.</summary>
        private void ReadItem(List<Token> item)
        {
            if (item.Count == 0) return;
            var start = item[0].Is("CONSTRAINT") ? 2 : 0;
            if (item.Count <= start) return;
            var tableConstraint = item[start].Is("PRIMARY") || item[start].Is("UNIQUE") || item[start].Is("CHECK") || item[start].Is("FOREIGN");
            var column = tableConstraint ? null : item[0].Text;

            var depth = 0;
            for (var j = start + (tableConstraint ? 0 : 1); j < item.Count; j++)
            {
                var t = item[j];
                if (t.IsPunct('(')) { depth++; continue; }
                if (t.IsPunct(')')) { depth--; continue; }
                if (t.Is("AUTOINCREMENT")) { Autoincrement = true; continue; } // also inside PRIMARY KEY (id AUTOINCREMENT)
                if (depth > 0 || t.Kind != Kind.Word) continue;

                if (t.Is("COLLATE") && column is not null && j + 1 < item.Count) Collations[column] = item[j + 1].Text;
                else if ((t.Is("CHECK") || t.Is("AS")) && j + 1 < item.Count && item[j + 1].IsPunct('('))
                {
                    var closing = Closing(item, j + 1);
                    if (closing < 0) continue;
                    var inner = Expression(item.GetRange(j + 2, closing - j - 2));
                    if (t.Is("CHECK")) Checks.Add(inner);
                    else if (column is not null)
                        Generated[column] = (closing + 1 < item.Count && item[closing + 1].Is("STORED") ? "STORED" : "VIRTUAL") + " AS " + inner;
                    j = closing;
                }
            }
        }
    }

    private static readonly Regex UniqueIndexStart = new(@"^\s*CREATE\s+UNIQUE\s+INDEX\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlainIndexStart = new(@"^\s*CREATE\s+INDEX\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when the baseline's index is UNIQUE and the database's is the same statement without UNIQUE.</summary>
    internal static bool DiffersOnlyByUniqueness(string referenceSql, string actualSql) =>
        UniqueIndexStart.IsMatch(referenceSql) && PlainIndexStart.IsMatch(actualSql)
        && Normalize(UniqueIndexStart.Replace(referenceSql, "CREATE INDEX")) == Normalize(actualSql);

    /// <summary>The name in <c>CREATE UNIQUE INDEX name ON ...</c>, or null.</summary>
    internal static string? UniqueIndexName(string statement)
    {
        var m = Regex.Match(statement, @"^\s*CREATE\s+UNIQUE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?[""`\[]?([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void CompareNamed(string kind, string drop, Dictionary<string, Named> reference, Dictionary<string, Named> actual, Diff diff)
    {
        foreach (var (name, r) in reference)
        {
            if (!actual.TryGetValue(name, out var a))
            {
                diff.Repairs.Add(r.Sql);
                diff.Repaired.Add(name);
            }
            else if (Normalize(a.Sql) != Normalize(r.Sql))
            {
                // A unique index that the database has WITHOUT the uniqueness (the first migration was corrected after the first nodes had run it, so
                // their index is the same index on the same columns, not unique, and real rows may already repeat): the program has always run on
                // it, and making it unique would fail on those rows. Left as it is.
                if (kind == "index" && DiffersOnlyByUniqueness(r.Sql, a.Sql))
                {
                    diff.Tolerated.Add($"the index {name} is not unique");
                    continue;
                }
                diff.Repairs.Add($"DROP {drop} {Quote(name)}");
                diff.Repairs.Add(r.Sql);
                diff.Repaired.Add(name);
            }
        }
    }
}

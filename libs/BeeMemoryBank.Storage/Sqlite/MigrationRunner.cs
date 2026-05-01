using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Sqlite;

public class MigrationRunner
{
    private readonly DbConnectionFactory _factory;

    public MigrationRunner(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task RunMigrationsAsync()
    {
        using var connection = _factory.CreateConnection();

        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS tbl_migration (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                version    INTEGER NOT NULL UNIQUE,
                filename   TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )");

        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r)
            .ToList();

        // Ghost Hunter: remove tbl_migration rows whose version has no
        // corresponding embedded *.sql resource in this assembly.
        // Makes squashing effortless: delete old migration files, update 001 —
        // existing DBs self-heal on next startup. No manual constants to touch.
        var assemblyVersions = resourceNames
            .Select(r => TryParseVersion(r, out var v) ? (int?)v : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToHashSet();

        var dbVersions = (await connection.QueryAsync<int>("SELECT version FROM tbl_migration")).ToList();
        var ghostVersions = dbVersions.Where(v => !assemblyVersions.Contains(v)).ToList();
        if (ghostVersions.Count > 0)
        {
            await connection.ExecuteAsync(
                "DELETE FROM tbl_migration WHERE version IN @ghostVersions",
                new { ghostVersions });
        }

        var applied = (await connection.QueryAsync<int>("SELECT version FROM tbl_migration"))
            .ToHashSet();

        foreach (var resourceName in resourceNames)
        {
            if (!TryParseVersion(resourceName, out var version)) continue;

            if (applied.Contains(version)) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            // Replace {{ENV:VAR_NAME}} placeholders with environment variable values.
            // If the variable is not set, substitutes NULL (SQL null literal).
            // Used by migrations that need runtime configuration (e.g. BMB_AGENT_DEFAULT_OWNER_ID).
            sql = Regex.Replace(sql, @"\{\{ENV:([A-Z0-9_]+)\}\}", m =>
                Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "NULL");

            var sqlStatements = SplitStatements(sql)
                .Select(StripComments)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Where(s => !s.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Migrations that DROP or RENAME tables need two PRAGMAs set before the transaction:
            //   foreign_keys = OFF       — disables FK *enforcement* so intermediate states
            //                              (e.g. rows in a child table pointing at a not-yet-
            //                              recreated parent) don't fail.
            //   legacy_alter_table = ON  — prevents SQLite from *auto-rewriting* FK references
            //                              in OTHER tables when we RENAME a table. Without this
            //                              flag, `ALTER TABLE parent RENAME TO parent_old`
            //                              silently rewrites every `REFERENCES parent(id)` in
            //                              unrelated child tables to `REFERENCES parent_old(id)`,
            //                              and when we then DROP parent_old the children are
            //                              left with dangling FKs pointing at a non-existent
            //                              table. This bit us in old migration 020.
            bool needsFkOff = sqlStatements.Any(s =>
                s.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("RENAME TO", StringComparison.OrdinalIgnoreCase));

            if (needsFkOff)
            {
                await connection.ExecuteAsync("PRAGMA foreign_keys = OFF");
                await connection.ExecuteAsync("PRAGMA legacy_alter_table = ON");
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var statement in sqlStatements)
                {
                    // Use a savepoint per statement so that idempotent failures (e.g. "duplicate
                    // column name" from ALTER TABLE ADD COLUMN on a column that already exists
                    // because the squashed 001_initial_schema.sql includes it) can be silently
                    // skipped without aborting the transaction.
                    await connection.ExecuteAsync("SAVEPOINT bmb_stmt", transaction: transaction);
                    try
                    {
                        await connection.ExecuteAsync(statement, transaction: transaction);
                        await connection.ExecuteAsync("RELEASE SAVEPOINT bmb_stmt", transaction: transaction);
                    }
                    catch (SqliteException ex) when (IsIdempotentError(statement, ex))
                    {
                        await connection.ExecuteAsync("ROLLBACK TO SAVEPOINT bmb_stmt", transaction: transaction);
                        await connection.ExecuteAsync("RELEASE SAVEPOINT bmb_stmt", transaction: transaction);
                        // Schema change already present — treat as applied and continue.
                    }
                }

                var now = DateTime.UtcNow.ToString("o");
                await connection.ExecuteAsync(
                    "INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (@version, @filename, @now, @now)",
                    new { version, filename = resourceName, now },
                    transaction: transaction);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                if (needsFkOff)
                {
                    await connection.ExecuteAsync("PRAGMA legacy_alter_table = OFF");
                    await connection.ExecuteAsync("PRAGMA foreign_keys = ON");
                }
            }
        }
    }

    /// <summary>
    /// Extracts the numeric version prefix from an embedded resource name.
    /// Resource names look like "BeeMemoryBank.Storage.Migrations.007_drop_duplicate.sql".
    /// The version segment is the first dot-separated part that starts with a digit.
    /// </summary>
    private static bool TryParseVersion(string resourceName, out int version)
    {
        var parts = resourceName.Split('.');
        var migPart = parts.FirstOrDefault(p => p.Length > 0 && char.IsDigit(p[0]));
        if (migPart == null) { version = 0; return false; }
        var versionStr = new string(migPart.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(versionStr, out version);
    }

    /// <summary>
    /// Returns true if the exception represents a schema change that is already
    /// present — i.e. the statement is safe to skip as a no-op.
    /// Currently handles:
    ///   ALTER TABLE ADD COLUMN → "duplicate column name" (column already exists)
    ///   CREATE TABLE           → "table ... already exists"
    ///   CREATE INDEX           → "index ... already exists"
    /// </summary>
    private static bool IsIdempotentError(string statement, SqliteException ex)
    {
        if (ex.SqliteErrorCode != 1) return false; // SQLITE_ERROR only

        var stmt = statement.TrimStart();
        if (stmt.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) &&
            stmt.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stmt.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) &&
            stmt.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stmt.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stmt.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var sb = new StringBuilder();
        var len = sql.Length;
        var i = 0;
        var depth = 0;
        var blockStack = new Stack<bool>();

        while (i < len)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < len && sql[i + 1] == '-')
            {
                while (i < len && sql[i] != '\n')
                {
                    sb.Append(sql[i]);
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < len && sql[i + 1] == '*')
            {
                sb.Append("/*");
                i += 2;
                while (i < len)
                {
                    if (sql[i] == '*' && i + 1 < len && sql[i + 1] == '/')
                    {
                        sb.Append("*/");
                        i += 2;
                        break;
                    }
                    sb.Append(sql[i]);
                    i++;
                }
                continue;
            }

            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < len)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        i++;
                        if (i < len && sql[i] == '\'')
                        {
                            sb.Append(sql[i]);
                            i++;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < len)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '"')
                    {
                        i++;
                        if (i < len && sql[i] == '"')
                        {
                            sb.Append(sql[i]);
                            i++;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                continue;
            }

            // SQLite accepts [bracketed] and `backtick` delimited identifiers in addition
            // to "double-quoted". Treat their contents as opaque so keywords inside
            // (e.g. CREATE TABLE [BEGIN]) can't skew BEGIN/END nesting.
            if (c == '[')
            {
                sb.Append(c);
                i++;
                while (i < len && sql[i] != ']')
                {
                    sb.Append(sql[i]);
                    i++;
                }
                if (i < len) { sb.Append(sql[i]); i++; }
                continue;
            }

            if (c == '`')
            {
                sb.Append(c);
                i++;
                while (i < len)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '`')
                    {
                        i++;
                        if (i < len && sql[i] == '`')
                        {
                            sb.Append(sql[i]);
                            i++;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == ';')
            {
                i++;
                if (depth == 0)
                {
                    var stmt = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(stmt))
                        statements.Add(stmt);
                    sb.Clear();
                }
                else
                {
                    sb.Append(';');
                }
                continue;
            }

            if (IsWordChar(c))
            {
                var wordStart = i;
                while (i < len && IsWordChar(sql[i]))
                    i++;

                var word = sql.Substring(wordStart, i - wordStart);

                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    blockStack.Push(true);
                    depth++;
                }
                else if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    blockStack.Push(false);
                }
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase) && blockStack.Count > 0)
                {
                    if (blockStack.Pop())
                        depth--;
                }

                sb.Append(word);
                continue;
            }

            sb.Append(c);
            i++;
        }

        var last = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(last))
            statements.Add(last);

        return statements;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string StripComments(string sql)
    {
        var lines = sql.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"));
        return string.Join('\n', lines).Trim();
    }
}

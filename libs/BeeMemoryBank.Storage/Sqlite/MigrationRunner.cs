using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// Applies the embedded migrations and keeps the ledger <c>tbl_migration (version, filename)</c>. Since 2.6.2 the vault has ONE migration, the
/// baseline <c>040_baseline.sql</c>: the whole schema as the 39 migrations of 2.6.0 / 2.6.1 left it, numbered 40 so that "schema version"
/// (snapshot manifests, join archives, blind packages) means the same before and after. Later migrations are numbered 41 and up.
///
/// <para>What the runner does with a database, decided from its ledger before anything is written (see <see cref="ProbeOnDisk"/>):
/// a new database gets the baseline and one ledger row; a ledger that matches the embedded files takes the normal path (apply what is
/// missing); the ledger of a 2.6.x database, exactly the 39 old rows, is ADOPTED: the baseline is not run, the database's structure is
/// checked against it, what is safely repairable is repaired, and the 39 rows become the baseline row, all in one transaction
/// (<see cref="LegacyChainAdoption"/>); anything else, that is a database of a release before 2.6, an interrupted update, a ledger nobody
/// knows, is refused with a <see cref="MigrationRefusedException"/> and not written to.</para>
///
/// <para>There is no "ghost hunter" any more: the old runner deleted a ledger row it had no file for and applied the file with that
/// number, which after a squash would run the baseline over a database of any age and skip every column an old migration would have
/// added. A later squash adds a recognised chain, as this one did; it does not guess.</para>
/// </summary>
public class MigrationRunner
{
    private readonly DbConnectionFactory _factory;
    private readonly ILogger? _logger;

    /// <summary>The number of the baseline: the highest migration of the old chain.</summary>
    public const int BaselineVersion = 40;

    /// <summary>The resource name of the baseline, which is also its ledger row's file name.</summary>
    public const string BaselineResourceName = LegacyMigrationChain.ResourcePrefix + "040_baseline.sql";

    public MigrationRunner(DbConnectionFactory factory, ILogger<MigrationRunner>? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task RunMigrationsAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r)
            .ToList();
        var assemblyByVersion = new Dictionary<int, string>();
        foreach (var r in resourceNames)
            if (TryParseVersion(r, out var v))
                assemblyByVersion[v] = r;

        // Read before writing: a database that is refused must be left exactly as it was: not given an empty ledger table, not switched to
        // WAL (the factory's connection initialization does that, and writes two header bytes of a rollback-journal file), not given
        // sidecars. So the ledger is read through a read-only connection that is not initialized at all, and the normal connection is
        // opened only for a database that is accepted.
        var verdict = ProbeOnDisk(assembly, assemblyByVersion);
        ThrowIfRefused(verdict);

        using var connection = _factory.CreateConnection();
        verdict ??= Classify(connection, assemblyByVersion); // the probe could not read it: the normal connection (it recovers a hot journal) decides
        ThrowIfRefused(verdict);
        if (verdict == LedgerVerdict.Adopt)
            await LegacyChainAdoption.AdoptAsync(connection, ReadBaseline(assembly), _logger);

        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS tbl_migration (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                version    INTEGER NOT NULL UNIQUE,
                filename   TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )");

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

            var sqlStatements = PrepareStatements(sql);

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
            //                              table.
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
                // The guard commits WITH the ledger row (see DowngradeGuardSql): there is no moment at which the row is there and the guard is not.
                await connection.ExecuteAsync(DowngradeGuardSql, transaction: transaction);

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

            _logger?.LogDebug("Applied migration {Version}: {File}", version, resourceName);

            // A migration that frees a lot of space (dropping a column that held every article
            // body, say) leaves the pages on SQLite's freelist — the file does not shrink until
            // VACUUM rewrites it, and VACUUM cannot run inside a transaction, so it cannot be a
            // statement of the migration itself. The migration opts in with a marker comment and
            // the rewrite happens here, once, right after its transaction has committed. Doubles
            // disk usage for its duration and takes seconds per hundred MB; on a migration that
            // just shed a third of the file that is the right trade.
            if (sql.Contains(VacuumAfterMarker, StringComparison.Ordinal))
            {
                // Best effort. The migration itself has committed; failing to reclaim space (no
                // room for VACUUM's temporary copy on a full phone, say) is not a reason to refuse
                // to start — and since the tbl_migration row is already written, a throw here would
                // only crash this one launch without ever being retried anyway.
                try { await connection.ExecuteAsync("VACUUM"); }
                catch (SqliteException) { }
            }
        }

        // Every start: a baseline row without its guard (a database that came from elsewhere, a tool that dropped it) gets it back. No write when it is there.
        await connection.ExecuteAsync(DowngradeGuardSql);
    }

    /// <summary>
    /// The runner of 2.6.1 and older meets the baseline row as a "ghost": it deletes it, then applies its own 39 files over a schema that has
    /// outgrown them. That re-runs the data migrations (012, 014, 015: key slots and agent keys are cleared again) and fails at 016
    /// ("no such column: ciphertext"), half way, with the ledger already rewritten, so that neither the old nor the new program would take
    /// the database afterwards. This trigger turns it into a refusal: the old runner's DELETE of the ledger row fails first, with this
    /// message, before it has written anything. It is created in the SAME transaction as the baseline row (the new database, the adoption), so
    /// there is no window, a crash or a lock-out included, in which the row exists without it; and every start re-creates it when it is
    /// missing. The current runner never deletes a ledger row except in the adoption, which drops it and creates it again inside its own
    /// transaction; a later release that has to delete rows does the same. Not part of the baseline (it belongs to the ledger, not to the
    /// vault), and ignored by the structure check of an adoption.
    /// </summary>
    internal const string DowngradeGuardName = "trg_migration_ledger_guard";

    internal const string DowngradeGuardSql = @"
        CREATE TRIGGER IF NOT EXISTS trg_migration_ledger_guard BEFORE DELETE ON tbl_migration
        BEGIN
            SELECT RAISE(ABORT, 'This database was written by version 2.6.2 or later of Bee Memory Bank and this older program cannot open it. Update the program, or restore a backup made by this version. Nothing was changed.');
        END";

    /// <summary>Put this in a comment in a migration to have the runner VACUUM after it commits.</summary>
    public const string VacuumAfterMarker = "bmb:vacuum-after";

    /// <summary>What the ledger of a database says about it.</summary>
    internal enum LedgerVerdict
    {
        /// <summary>No ledger rows and nothing in the database: a new one, the baseline goes in.</summary>
        New,
        /// <summary>The rows are the embedded files' (some of them): the normal path applies what is missing.</summary>
        Current,
        /// <summary>Exactly the 39 rows of 2.6.0 / 2.6.1: adopt.</summary>
        Adopt,
        /// <summary>A part of the old chain, or no rows on a database that has tables: a release before 2.6, or an update that stopped.</summary>
        Older,
        /// <summary>Rows nobody here knows.</summary>
        Unknown,
    }

    /// <summary>Reads the ledger and says what it means. Reads only: nothing is created, nothing is written.</summary>
    internal static LedgerVerdict Classify(System.Data.IDbConnection connection, IReadOnlyDictionary<int, string> assemblyByVersion,
        System.Data.IDbTransaction? transaction = null)
    {
        var hasLedger = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_migration'", transaction: transaction) > 0;
        var rows = hasLedger
            ? connection.Query<MigrationRow>("SELECT version AS Version, filename AS Filename FROM tbl_migration", transaction: transaction).ToList()
            : [];

        if (rows.Count == 0)
        {
            // A new database has nothing but, perhaps, an empty ledger. Tables without a history prove nothing about their state.
            var objects = connection.ExecuteScalar<long>(
                @"SELECT COUNT(*) FROM sqlite_master
                  WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\' AND name <> 'tbl_migration' AND tbl_name <> 'tbl_migration'", transaction: transaction);
            return objects > 0 ? LedgerVerdict.Older : LedgerVerdict.New;
        }

        if (rows.All(r => assemblyByVersion.TryGetValue(r.Version, out var file) && file == r.Filename))
            return LedgerVerdict.Current;

        var ledger = rows.Select(r => (r.Version, r.Filename)).ToHashSet();
        var legacy = LegacyMigrationChain.LedgerRows.ToHashSet();
        if (ledger.SetEquals(legacy)) return LedgerVerdict.Adopt;
        return ledger.IsSubsetOf(legacy) ? LedgerVerdict.Older : LedgerVerdict.Unknown;
    }

    /// <summary>The verdict on a ledger read inside a transaction, against the files embedded in this program (the adoption asks it once it holds the write lock).</summary>
    internal static LedgerVerdict ClassifyEmbedded(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) =>
        Classify(connection, EmbeddedByVersion(Assembly.GetExecutingAssembly()), transaction);

    private static Dictionary<int, string> EmbeddedByVersion(Assembly assembly)
    {
        var result = new Dictionary<int, string>();
        foreach (var r in assembly.GetManifestResourceNames().Where(r => r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
            if (TryParseVersion(r, out var v))
                result[v] = r;
        return result;
    }

    /// <summary>
    /// Whether this program would open the database behind <paramref name="connection"/>, judged from its ledger alone (the restore of a
    /// backup asks it before it replaces anything): null when it would (a 2.6.x database is adopted, a current one is used), the refusal
    /// the runner would give otherwise. Reads only.
    /// </summary>
    public static MigrationRefusedException? JudgeLedger(System.Data.IDbConnection connection) =>
        Classify(connection, EmbeddedByVersion(Assembly.GetExecutingAssembly())) switch
        {
            LedgerVerdict.Older => MigrationRefusedException.Older(),
            LedgerVerdict.Unknown => MigrationRefusedException.Unknown(),
            _ => null,
        };

    /// <summary>
    /// The runner's decision about a database, ledger AND structure, as a question (a backup staged for a restore is asked it before anything
    /// is replaced): null when this program would open it, the refusal it would give otherwise. A 2.6.x ledger is adopted, so what an adoption
    /// repairs does not count; a ledger that is the baseline's alone is not repaired by anything, so there everything counts. A ledger with
    /// rows after the baseline (a later migration) is not compared with the baseline's structure, which that migration changed. Reads only.
    /// </summary>
    public static MigrationRefusedException? JudgeDatabase(System.Data.IDbConnection connection)
    {
        var assembly = Assembly.GetExecutingAssembly();
        switch (Classify(connection, EmbeddedByVersion(assembly)))
        {
            case LedgerVerdict.Older: return MigrationRefusedException.Older();
            case LedgerVerdict.Unknown: return MigrationRefusedException.Unknown();
            case LedgerVerdict.Adopt: return LegacyChainAdoption.Judge(connection, ReadBaseline(assembly));
            case LedgerVerdict.Current when connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_migration WHERE version <> @baseline", new { baseline = BaselineVersion }) == 0:
                return LegacyChainAdoption.JudgeAsIs(connection, ReadBaseline(assembly));
            default: return null;
        }
    }

    /// <summary>
    /// The verdict on the database file, read through a read-only connection that is opened without the factory's initialization (no
    /// PRAGMA, so nothing is written: not the journal mode in the header, no sidecar). A file that is not there or is empty is a new
    /// database. When nothing is in a WAL or a journal the file is read as immutable (no lock, no sidecar at all); when something is, the
    /// engine reads through them (they exist already). For the ledger of a 2.6.x database the structure is checked here too, so that a
    /// database that would be refused for its structure is refused before the normal connection is opened. Null: the probe could not read
    /// the file (a hot journal that needs recovery, say): the normal connection, which can recover it, decides then.
    /// </summary>
    private LedgerVerdict? ProbeOnDisk(Assembly assembly, IReadOnlyDictionary<int, string> assemblyByVersion)
    {
        var path = _factory.FilePath;
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return LedgerVerdict.New;
        var quiet = !HasContent(path + "-wal") && !HasContent(path + "-journal");
        try
        {
            using var probe = _factory.OpenProbe(path, immutable: quiet);
            var verdict = Classify(probe, assemblyByVersion);
            if (verdict == LedgerVerdict.Adopt && LegacyChainAdoption.Judge(probe, ReadBaseline(assembly)) is { } refusal)
                throw refusal;
            return verdict;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static void ThrowIfRefused(LedgerVerdict? verdict)
    {
        if (verdict == LedgerVerdict.Older) throw MigrationRefusedException.Older();
        if (verdict == LedgerVerdict.Unknown) throw MigrationRefusedException.Unknown();
    }

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static string ReadBaseline(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(BaselineResourceName)
            ?? throw new InvalidOperationException("The baseline migration is not embedded in this program.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A migration file as the statements the runner executes: split, comments and PRAGMAs removed.</summary>
    internal static List<string> PrepareStatements(string sql) =>
        SplitStatements(sql)
            .Select(StripComments)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !s.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
            .ToList();

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

        // CREATE TABLE / INDEX / UNIQUE INDEX / TRIGGER / VIEW — any "already exists"
        // is treated as idempotent. Re-applying a squashed 001_initial_schema on top
        // of a DB that already has the full schema lights up every one of these.
        if (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
            (stmt.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)))
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

    private class MigrationRow
    {
        public int Version { get; set; }
        public string Filename { get; set; } = "";
    }

    private static string StripComments(string sql)
    {
        var lines = sql.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"));
        return string.Join('\n', lines).Trim();
    }
}

using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314 fix S3: the adoption compares the whole required shape of a table, not a subset of it: DEFAULT expressions, CHECK constraints,
/// WITHOUT ROWID, STRICT, AUTOINCREMENT, the collation of a column and generated columns. Each test builds a "2.6.x" database from the
/// embedded baseline with ONE edit in one table (the baseline is the schema of the final release, so everything else is exactly as it should
/// be) and gives it the ledger of the old chain. A difference that changes what the program reads or writes is refused and the file is left
/// as it was; a difference of text only, or an extra the program does not use, is adopted. Real histories (the ALTER TABLE text of
/// older releases) are guarded by <c>ReleasedVersionsTests</c>.
/// </summary>
public class ShapeSemanticsTests : SquashTestBase
{
    private const string LedgerDdl = @"CREATE TABLE IF NOT EXISTS tbl_migration (
        id INTEGER PRIMARY KEY AUTOINCREMENT, version INTEGER NOT NULL UNIQUE, filename TEXT NOT NULL, applied_at TEXT NOT NULL, updated_at TEXT NOT NULL)";

    private static string Baseline()
    {
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(BaselineName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>The baseline with <paramref name="find"/> replaced inside the CREATE TABLE statement of <paramref name="table"/>, and nowhere else.</summary>
    private static string EditTable(string text, string table, string find, string replace)
    {
        var start = text.IndexOf($"CREATE TABLE {table} (", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{table} is in the baseline");
        var end = text.IndexOf(";\n", start, StringComparison.Ordinal);
        var statement = text[start..end];
        statement.Split(find).Length.Should().Be(2, $"'{find}' is in {table} exactly once, so the variant differs in one place");
        return text[..start] + statement.Replace(find, replace) + text[end..];
    }

    /// <summary>A database whose schema is the baseline with <paramref name="edits"/> applied, carrying the 39 ledger rows of release 2.6.1.</summary>
    private string Variant(params (string Table, string Find, string Replace)[] edits)
    {
        var text = Baseline();
        foreach (var (table, find, replace) in edits) text = EditTable(text, table, find, replace);
        var path = NewDbPath();
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
        {
            foreach (var statement in MigrationRunner.PrepareStatements(text))
                conn.Execute(statement);
            conn.Execute(LedgerDdl);
            SetLedger(conn, LegacyChain.LedgerOf("v2.6.1"));
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    // ---- differences that are refused, one test (one theory row) each ------------------------------------------------------------------

    [Theory]
    [InlineData("a changed default", "tbl_folder", "DEFAULT 'A'", "DEFAULT 'X'", "tbl_folder.status")]
    [InlineData("a default that is gone (and NOT NULL with it)", "tbl_folder", "status                  TEXT NOT NULL DEFAULT 'A'", "status TEXT", "tbl_folder.status")]
    [InlineData("a default where the baseline has none", "tbl_folder", "parent_path             TEXT,", "parent_path             TEXT DEFAULT '/',", "tbl_folder.parent_path")]
    [InlineData("a CHECK on a column that is gone", "tbl_folder_acl_entry", " CHECK(effect IN ('allow', 'deny'))", "", "tbl_folder_acl_entry")]
    [InlineData("a CHECK on a column that says something else", "tbl_role", "CHECK(base_policy IN ('open', 'closed'))", "CHECK(base_policy IN ('open', 'closed', 'both'))", "tbl_role")]
    [InlineData("a table CHECK that is gone", "tbl_concept_tag_edge", ",\n    CHECK (tag_id_a < tag_id_b)", "", "tbl_concept_tag_edge")]
    [InlineData("a table that is not WITHOUT ROWID", "tbl_concept_tag_edge", ") WITHOUT ROWID", ")", "tbl_concept_tag_edge")]
    [InlineData("a table that is STRICT", "tbl_concept_tag_edge", ") WITHOUT ROWID", ") WITHOUT ROWID, STRICT", "tbl_concept_tag_edge")]
    [InlineData("an AUTOINCREMENT that is gone", "tbl_user", "id            INTEGER PRIMARY KEY AUTOINCREMENT", "id            INTEGER PRIMARY KEY", "tbl_user")]
    [InlineData("a unique column that lost its collation", "tbl_user", "UNIQUE COLLATE NOCASE", "UNIQUE", "tbl_user.username")]
    [InlineData("a primary key column that lost its collation", "tbl_role", "TEXT PRIMARY KEY COLLATE NOCASE", "TEXT PRIMARY KEY", "tbl_role.name")]
    [InlineData("a plain column that has a collation", "tbl_folder", "path                    TEXT NOT NULL", "path                    TEXT NOT NULL COLLATE NOCASE", "tbl_folder.path")]
    [InlineData("a column that is generated (virtual)", "tbl_media", "\n, kind TEXT NOT NULL DEFAULT 'image'", "\n, kind TEXT GENERATED ALWAYS AS ('image') VIRTUAL", "tbl_media.kind is generated")]
    [InlineData("a column that is generated (stored)", "tbl_media", "\n, kind TEXT NOT NULL DEFAULT 'image'", "\n, kind TEXT AS ('image') STORED", "tbl_media.kind is generated")]
    [InlineData("an extra column that is NOT NULL and has no default", "tbl_media", "CREATE TABLE tbl_media (", "CREATE TABLE tbl_media (\n    extra_required TEXT NOT NULL,", "tbl_media.extra_required")]
    public async Task A_difference_in_what_the_table_means_is_refused_and_the_file_is_untouched(
        string what, string table, string find, string replace, string messageNames)
    {
        var path = Variant((table, find, replace));

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain(messageNames, what);
        ex.Message.Should().NotContain(Dir, "the refusal names objects, never paths");
    }

    // ---- differences that are not differences --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_constraints_written_another_way_are_adopted()
    {
        // The text of a table in sqlite_master is what the author (or an ALTER TABLE) wrote: spacing, case, redundant parentheses and comments
        // are not a different table.
        var path = Variant(
            ("tbl_role", "CHECK(base_policy IN ('open', 'closed'))", "check ( base_policy   in ( 'open' ,'closed' ) ) /* the policy */"),
            ("tbl_role", "DEFAULT 'closed'", "DEFAULT ( 'closed' )"),
            ("tbl_role", "TEXT PRIMARY KEY COLLATE NOCASE,", "TEXT PRIMARY KEY collate nocase, -- case-blind names"),
            ("tbl_concept_tag_edge", ") WITHOUT ROWID", ") without   rowid"),
            ("tbl_folder_acl_entry", "CHECK(effect IN ('allow', 'deny'))", "CHECK((effect IN ('allow','deny')))"));

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }

    [Fact]
    public async Task A_column_that_sits_in_another_place_is_the_same_column()
    {
        // What ALTER TABLE ADD COLUMN leaves behind: the column at the end, the constraints the same.
        var path = Variant(
            ("tbl_folder", "    status                  TEXT NOT NULL DEFAULT 'A',\n", ""),
            ("tbl_folder", "CREATE TABLE tbl_folder (", "CREATE TABLE tbl_folder (\n    status TEXT NOT NULL DEFAULT 'A',"));

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }

    [Fact]
    public async Task Extra_columns_the_program_does_not_write_are_adopted()
    {
        // Nullable, NOT NULL with a default, and generated: none of them can make an INSERT of the program fail.
        var path = Variant(
            ("tbl_media", "CREATE TABLE tbl_media (", "CREATE TABLE tbl_media (\n    extra_plain TEXT,\n    extra_defaulted INTEGER NOT NULL DEFAULT 7,\n    extra_generated TEXT AS (id || '!') VIRTUAL,"));

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }

    [Fact]
    public async Task Another_spelling_of_a_type_and_a_looser_NOT_NULL_are_still_tolerated()
    {
        var path = Variant(
            ("tbl_media", "file_size      INTEGER NOT NULL", "file_size      BIGINT NOT NULL"),
            ("tbl_media", "file_name      TEXT NOT NULL", "file_name      VARCHAR(255)"));
        var logger = new CapturingLogger<MigrationRunner>();

        await RunNewRunnerAsync(path, logger);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information)
            .Which.Message.Should().Contain("column difference");
    }
}

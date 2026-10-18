using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314 fix S4: <c>MigrationRunner.JudgeDatabase</c> is the runner's decision about a database, ledger and structure, as a question the
/// restore of a backup asks of the staged copy before anything is replaced. A backup with the ledger of a 2.6.x node is judged like the
/// adoption would (what an adoption repairs is no reason to refuse it); a backup with the baseline's ledger is not repaired by anything at
/// the next start, so a gap in it is a reason to refuse. The ledger-only question stays in <c>JudgeLedgerTests</c>.
/// </summary>
public class JudgeDatabaseTests : SquashTestBase
{
    private static MigrationRefusedException? Judge(string path)
    {
        SqliteConnection.ClearAllPools();
        MigrationRefusedException? refusal;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            refusal = MigrationRunner.JudgeDatabase(conn);
        SqliteConnection.ClearAllPools();
        return refusal;
    }

    private async Task<string> BaselineLedgerDbAsync() => await CurrentSchemaDbAsync(NewDbPath());

    private async Task<string> LegacyLedgerDbAsync()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        SetLedger(path, LegacyChain.LedgerOf("v2.6.1"));
        return path;
    }

    [Fact]
    public async Task A_complete_database_with_either_ledger_is_judged_fine()
    {
        Judge(await BaselineLedgerDbAsync()).Should().BeNull();
        Judge(await LegacyLedgerDbAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_2_6_backup_with_a_column_missing_is_refused_by_name()
    {
        var path = await LegacyLedgerDbAsync();
        Run(path, "ALTER TABLE tbl_node_identity DROP COLUMN brand_name");

        var refusal = Judge(path);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(MigrationRefusal.IncompatibleStructure);
        refusal.Problems.Should().Contain(p => p.Contains("tbl_node_identity.brand_name"));
    }

    [Fact]
    public async Task A_baseline_backup_with_a_column_missing_is_refused_by_name()
    {
        var path = await BaselineLedgerDbAsync();
        Run(path, "ALTER TABLE tbl_node_identity DROP COLUMN brand_name");

        var refusal = Judge(path);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(MigrationRefusal.IncompatibleStructure);
        refusal.Problems.Should().Contain(p => p.Contains("tbl_node_identity.brand_name"));
    }

    [Fact]
    public async Task A_2_6_backup_with_an_index_missing_is_fine_because_the_adoption_repairs_it()
    {
        var path = await LegacyLedgerDbAsync();
        Run(path, "DROP INDEX idx_article_version_article");

        Judge(path).Should().BeNull();
    }

    [Fact]
    public async Task A_baseline_backup_with_an_index_missing_is_refused_because_nothing_would_repair_it()
    {
        var path = await BaselineLedgerDbAsync();
        Run(path, "DROP INDEX idx_article_version_article");

        var refusal = Judge(path);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(MigrationRefusal.IncompatibleStructure);
        refusal.Problems.Should().Contain(p => p.Contains("idx_article_version_article"));
    }

    [Fact]
    public async Task A_baseline_backup_with_a_table_missing_is_refused()
    {
        var path = await BaselineLedgerDbAsync();
        Run(path, "DROP TABLE tbl_node_data_key");

        var refusal = Judge(path);

        refusal!.Reason.Should().Be(MigrationRefusal.IncompatibleStructure);
        refusal.Problems.Should().Contain(p => p.Contains("tbl_node_data_key"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_backup_whose_default_differs_is_refused_with_either_ledger(bool legacyLedger)
    {
        var path = legacyLedger ? await LegacyLedgerDbAsync() : await BaselineLedgerDbAsync();
        Run(path, @"PRAGMA writable_schema = ON;
                    UPDATE sqlite_master SET sql = replace(sql, 'DEFAULT ''A''', 'DEFAULT ''X''') WHERE type = 'table' AND name = 'tbl_folder';
                    PRAGMA writable_schema = OFF");

        var refusal = Judge(path);

        refusal.Should().NotBeNull();
        refusal!.Problems.Should().Contain(p => p.Contains("tbl_folder.status"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_backup_with_extras_is_fine_with_either_ledger(bool legacyLedger)
    {
        var path = legacyLedger ? await LegacyLedgerDbAsync() : await BaselineLedgerDbAsync();
        Run(path, @"CREATE TABLE tbl_tag (id INTEGER PRIMARY KEY, name TEXT);
                    CREATE INDEX ix_leftover ON tbl_tag(name);
                    ALTER TABLE tbl_folder ADD COLUMN leftover_note TEXT");

        Judge(path).Should().BeNull();
    }

    [Fact]
    public async Task The_ledger_is_still_judged_first()
    {
        var older = await CurrentSchemaDbAsync(NewDbPath("older.db"));
        SetLedger(older, LegacyChain.LedgerOf("v2.5.0"));
        var unknown = await CurrentSchemaDbAsync(NewDbPath("unknown.db"));
        SetLedger(unknown, LegacyChain.LedgerOf("v2.6.1").Append((41, "BeeMemoryBank.Storage.Migrations.041_later.sql")));

        Judge(older)!.Reason.Should().Be(MigrationRefusal.OlderDatabase);
        Judge(unknown)!.Reason.Should().Be(MigrationRefusal.UnknownHistory);
    }
}

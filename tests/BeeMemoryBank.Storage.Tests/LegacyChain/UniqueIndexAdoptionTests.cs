using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// The first migration was corrected after the first nodes had run it: their index of the article versions is the same index on the same
/// columns, but NOT unique, and a real node (the owner's, found on release day) has two rows that repeat one (article, version number).
/// Adoption built the baseline's UNIQUE index, failed on those rows and rolled back, so the node could not be opened at all. A unique index
/// holds no data of its own and the program has always run without the uniqueness: it must never stop a node.
/// </summary>
public class UniqueIndexAdoptionTests : SquashTestBase
{
    private const string TwoRowsOfOneVersion = @"PRAGMA foreign_keys = OFF;
        INSERT INTO tbl_article_version (id, article_id, version_number, title, tree_path, iv, encrypted_dek, dek_iv, created_at)
            VALUES ('v-a', 'art-1', 7, 't', '/t', X'01', X'02', X'03', '2026-10-01T10:00:00.0000000Z');
        INSERT INTO tbl_article_version (id, article_id, version_number, title, tree_path, iv, encrypted_dek, dek_iv, created_at)
            VALUES ('v-b', 'art-1', 7, 't', '/t', X'01', X'02', X'03', '2026-10-01T10:00:01.0000000Z');";

    private async Task<string> A26DatabaseAsync()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        SeedRows(path);
        SetLedger(path, LegacyChain.LedgerOf("v2.6.1"));
        return path;
    }

    private static string IndexSql(string path) =>
        Query<string>(path, "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'idx_article_version_article'");

    [Fact]
    public async Task A_not_unique_index_with_repeated_rows_is_adopted_and_left_as_it_is()
    {
        var path = await A26DatabaseAsync();
        Run(path, "DROP INDEX idx_article_version_article");
        Run(path, "CREATE INDEX idx_article_version_article ON tbl_article_version(article_id, version_number DESC)");
        Run(path, TwoRowsOfOneVersion);
        var logger = new CapturingLogger<MigrationRunner>();

        await RunNewRunnerAsync(path, logger);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        IndexSql(path).Should().NotContain("UNIQUE", "the index stays as the database had it");
        Query<long>(path, "SELECT COUNT(*) FROM tbl_article_version WHERE article_id = 'art-1' AND version_number = 7").Should().Be(2, "no row is touched");
        Query<string>(path, "PRAGMA integrity_check").Should().Be("ok");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_not_unique_index_without_repeats_is_also_left_as_it_is()
    {
        var path = await A26DatabaseAsync();
        Run(path, "DROP INDEX idx_article_version_article");
        Run(path, "CREATE INDEX idx_article_version_article ON tbl_article_version(article_id, version_number DESC)");

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        IndexSql(path).Should().NotContain("UNIQUE");
    }

    [Fact]
    public async Task A_missing_unique_index_that_the_rows_cannot_satisfy_stays_missing_and_the_database_is_adopted()
    {
        var path = await A26DatabaseAsync();
        Run(path, "DROP INDEX idx_article_version_article");
        Run(path, TwoRowsOfOneVersion);
        var before = Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index'");

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'idx_article_version_article'").Should().Be(0, "the repair was undone, the rest of the adoption went on");
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index'").Should().Be(before);
        Query<long>(path, "SELECT COUNT(*) FROM tbl_article_version").Should().BeGreaterThanOrEqualTo(2);
        Query<string>(path, "PRAGMA integrity_check").Should().Be("ok");

        // The next start is the normal path (the baseline's ledger is there; nothing is adopted or repaired again).
        await RunNewRunnerAsync(path);
        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }

    [Fact]
    public async Task A_missing_unique_index_that_the_rows_can_satisfy_is_still_created()
    {
        var path = await A26DatabaseAsync();
        Run(path, "DROP INDEX idx_article_version_article");

        await RunNewRunnerAsync(path);

        IndexSql(path).Should().Contain("UNIQUE");
    }
}

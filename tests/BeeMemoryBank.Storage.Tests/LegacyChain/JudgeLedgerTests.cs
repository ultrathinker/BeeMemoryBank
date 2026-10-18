using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// <c>MigrationRunner.JudgeLedger</c> is the runner's decision as a question (the restore of a backup asks it before it replaces the node):
/// would this version open the database? It reads and writes nothing.
/// </summary>
public class JudgeLedgerTests : SquashTestBase
{
    private static IEnumerable<(int, string)> Legacy(int rows) => LegacyChain.LedgerOf("v2.6.1").Take(rows);

    private async Task<MigrationRefusedException?> JudgeAsync(Action<string>? arrange = null)
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        arrange?.Invoke(path);
        using var factory = new DbConnectionFactory(path);
        using var conn = factory.CreateConnection();
        return MigrationRunner.JudgeLedger(conn);
    }

    [Fact]
    public async Task A_database_with_the_ledger_of_this_version_is_accepted() =>
        (await JudgeAsync()).Should().BeNull();

    [Fact]
    public async Task A_database_with_the_39_rows_of_2_6_x_is_accepted_it_is_adopted_at_the_next_start() =>
        (await JudgeAsync(p => SetLedger(p, Legacy(39)))).Should().BeNull();

    [Theory]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(20)]
    public async Task A_part_of_the_old_chain_is_older_than_2_6(int rows)
    {
        var refusal = await JudgeAsync(p => SetLedger(p, Legacy(rows)));

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(MigrationRefusal.OlderDatabase);
        refusal.Message.Should().Contain("older than 2.6");
    }

    [Fact]
    public async Task Rows_nobody_knows_are_an_unknown_history()
    {
        var refusal = await JudgeAsync(p => SetLedger(p, Legacy(39).Append((41, "BeeMemoryBank.Storage.Migrations.041_later.sql"))));

        refusal!.Reason.Should().Be(MigrationRefusal.UnknownHistory);
    }

    [Fact]
    public async Task Tables_without_any_ledger_row_are_older_than_2_6()
    {
        var refusal = await JudgeAsync(p => SetLedger(p, []));

        refusal!.Reason.Should().Be(MigrationRefusal.OlderDatabase);
    }

    [Fact]
    public async Task Judging_writes_nothing()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        SetLedger(path, Legacy(20));
        var before = SchemaDump.FileDigest(path);

        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            MigrationRunner.JudgeLedger(conn).Should().NotBeNull();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        SchemaDump.FileDigest(path).Should().Be(before);
    }
}

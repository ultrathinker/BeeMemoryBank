using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314 fix S2: the guard trigger that makes the runner of 2.6.1 refuse an adopted database is committed WITH the baseline row, not after
/// it. A start that dies, or is locked out, between the two would leave a committed baseline row without protection, and the old runner
/// would then delete it as a ghost and replay its chain over the live vault. The crash is simulated with a logger that throws: the runner
/// writes its log line right after the commit, so everything it does after that point is what a crash there would skip.
/// </summary>
public class GuardWindowTests : SquashTestBase
{
    private const string Guard = "trg_migration_ledger_guard";

    /// <summary>Throws as soon as the runner logs at or above <paramref name="from"/>: "the process died right after the commit".</summary>
    private sealed class CrashingLogger(LogLevel from) : ILogger<MigrationRunner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= from) throw new InvalidOperationException("the start died here (test)");
        }
    }

    private static long GuardCount(string path) =>
        Query<long>(path, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = '{Guard}' AND tbl_name = 'tbl_migration'");

    private static async Task DieAfterTheCommitAsync(string path, LogLevel from)
    {
        using (var factory = new DbConnectionFactory(path))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(factory, new CrashingLogger(from)).RunMigrationsAsync());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task An_adoption_that_dies_right_after_its_commit_leaves_the_baseline_row_with_its_guard()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        SetLedger(path, LegacyChain.LedgerOf("v2.6.1"));

        await DieAfterTheCommitAsync(path, LogLevel.Information);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)], "the adoption had committed");
        GuardCount(path).Should().Be(1, "the guard commits with the baseline row; a start that dies after it leaves a protected database");
    }

    [Fact]
    public async Task A_new_database_whose_start_dies_right_after_the_baseline_commit_has_the_guard_with_it()
    {
        var path = NewDbPath();

        await DieAfterTheCommitAsync(path, LogLevel.Debug);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)], "the baseline had committed");
        GuardCount(path).Should().Be(1, "ledger row and guard are one commit");
    }

    [Fact]
    public async Task A_baseline_database_without_the_guard_gets_it_back_at_the_next_start()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, $"DROP TRIGGER {Guard}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GuardCount(path).Should().Be(0);

        await RunNewRunnerAsync(path);

        GuardCount(path).Should().Be(1, "every start checks that the guard is there when the baseline row is");
    }

    [Fact]
    public async Task The_guard_is_still_there_after_an_adoption_that_replaced_a_ledger_that_had_one()
    {
        // A restored backup of a 2.6.2 database whose ledger a tool turned back into the 39 old rows keeps its guard trigger: the adoption must
        // not trip over it (its own DELETE is the one delete the guard must let through).
        var path = await CurrentSchemaDbAsync(NewDbPath());
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
        {
            conn.Execute("DROP TRIGGER " + Guard);
            SetLedger(conn, LegacyChain.LedgerOf("v2.6.1"));
            conn.Execute(MigrationRunner.DowngradeGuardSql);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        GuardCount(path).Should().Be(1);
    }
}

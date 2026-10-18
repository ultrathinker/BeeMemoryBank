using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314 fix S5: two programs that start on the same 2.6.x database (the CLI next to a running node, two containers on one volume) both
/// read the 39 old rows and both decide to adopt. The second must find the first one's work done and carry on, not fail with a busy-snapshot
/// error that surfaces as "adoption failed". The adoption takes the write lock before it decides anything and decides again under it.
/// </summary>
public class ConcurrentAdoptionTests : SquashTestBase
{
    private async Task<string> A26DatabaseAsync()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        SetLedger(path, LegacyChain.LedgerOf("v2.6.1"));
        return path;
    }

    [Fact]
    public async Task A_runner_that_waits_for_the_write_lock_while_another_adoption_commits_carries_on()
    {
        var path = await A26DatabaseAsync();
        var logger = new CapturingLogger<MigrationRunner>();

        // The "first process": it holds the write lock (BEGIN IMMEDIATE) while the second one starts, does the adoption's work, and commits.
        using var first = new SqliteConnection($"Data Source={path};Pooling=False");
        first.Open();
        first.Execute("BEGIN IMMEDIATE");

        var second = Task.Run(() => RunNewRunnerAsync(path, logger));
        await Task.Delay(1500); // long enough for the second to read the old rows, decide to adopt and run into the lock
        second.IsCompleted.Should().BeFalse("it is waiting for the write lock the first one holds");

        first.Execute("DELETE FROM tbl_migration");
        first.Execute("INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (40, @name, '2026-10-18T00:00:00Z', '2026-10-18T00:00:00Z')", new { name = BaselineName });
        first.Execute(MigrationRunner.DowngradeGuardSql);
        first.Execute("COMMIT");

        await second; // must not throw
        SqliteConnection.ClearAllPools();

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        logger.Entries.Should().NotContain(e => e.Message.Contains("Adopted"), "the second runner found the adoption done and wrote nothing of its own");
    }

    [Fact]
    public async Task A_runner_whose_wait_for_the_write_lock_outlasts_the_busy_timeout_keeps_waiting_and_carries_on()
    {
        // The first adoption takes longer than the connection's busy timeout (5 s: a big vault, a full-text index to rebuild). The second
        // runner must not give up with "adoption failed"; it looks again, sees that the adoption is under way, waits for it, and finds it done.
        var path = await A26DatabaseAsync();
        var logger = new CapturingLogger<MigrationRunner>();
        using var first = new SqliteConnection($"Data Source={path};Pooling=False");
        first.Open();
        first.Execute("BEGIN IMMEDIATE");

        var second = Task.Run(() => RunNewRunnerAsync(path, logger));
        await Task.Delay(7000); // past the busy timeout of the second runner's connection
        second.IsCompleted.Should().BeFalse("the second runner is still waiting, not failed");

        first.Execute("DELETE FROM tbl_migration");
        first.Execute("INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (40, @name, '2026-10-18T00:00:00Z', '2026-10-18T00:00:00Z')", new { name = BaselineName });
        first.Execute(MigrationRunner.DowngradeGuardSql);
        first.Execute("COMMIT");

        await second; // must not throw
        SqliteConnection.ClearAllPools();

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        logger.Entries.Should().NotContain(e => e.Message.Contains("Adopted"));
    }

    [Fact]
    public async Task An_adoption_whose_connection_gives_up_on_the_lock_sooner_than_the_other_one_takes_looks_again_and_carries_on()
    {
        // The wait for the write lock is the connection's busy timeout (30 s by default, 5 s if only the factory's PRAGMA counted); when the other
        // adoption takes longer the second must not fail, it must look again. Here the second connection gives up after 1 s, the first
        // commits after 3 s: before the fix the second reported "adoption failed", now it sees the lock is busy, looks at the ledger, waits on.
        var path = await A26DatabaseAsync();
        string baselineSql;
        using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(BaselineName)!)
        using (var reader = new StreamReader(stream))
            baselineSql = await reader.ReadToEndAsync();
        var logger = new CapturingLogger<MigrationRunner>();
        using var first = new SqliteConnection($"Data Source={path};Pooling=False");
        first.Open();
        first.Execute("BEGIN IMMEDIATE");

        using var second = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=1");
        second.Open();
        var adopting = Task.Run(() => LegacyChainAdoption.AdoptAsync(second, baselineSql, logger));
        await Task.Delay(3000);
        adopting.IsCompleted.Should().BeFalse("still waiting for the lock, not failed");

        first.Execute("DELETE FROM tbl_migration");
        first.Execute("INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (40, @name, '2026-10-18T00:00:00Z', '2026-10-18T00:00:00Z')", new { name = BaselineName });
        first.Execute(MigrationRunner.DowngradeGuardSql);
        first.Execute("COMMIT");

        await adopting; // must not throw
        second.Close();
        SqliteConnection.ClearAllPools();

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        logger.Entries.Should().NotContain(e => e.Message.Contains("Adopted"));
    }

    [Fact]
    public async Task Two_runners_started_together_on_one_2_6_database_both_succeed_and_one_of_them_adopts()
    {
        for (var round = 0; round < 6; round++)
        {
            var path = await CurrentSchemaDbAsync(NewDbPath($"race{round}.db"));
            SetLedger(path, LegacyChain.LedgerOf("v2.6.1"));
            var loggerA = new CapturingLogger<MigrationRunner>();
            var loggerB = new CapturingLogger<MigrationRunner>();

            await Task.WhenAll(
                Task.Run(() => RunNewRunnerAsync(path, loggerA)),
                Task.Run(() => RunNewRunnerAsync(path, loggerB)));

            ReadAll(path).Ledger.Should().Equal([(40, BaselineName)], $"round {round}");
            (loggerA.Entries.Concat(loggerB.Entries)).Count(e => e.Message.Contains("Adopted")).Should().Be(1, $"round {round}: exactly one of them adopted");
        }
    }
}

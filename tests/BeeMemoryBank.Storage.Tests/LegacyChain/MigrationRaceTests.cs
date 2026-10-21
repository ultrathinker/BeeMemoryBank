using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// Two programs that start together on a database that lacks migrations 041 / 042 (the CLI next to a node, two containers on one volume).
/// Both read the ledger, both decide that 041 is missing; the second one gets the write lock after the first has committed, and must find the
/// row there instead of running the migration again and failing on <c>UNIQUE constraint failed: tbl_migration.version</c>.
/// </summary>
public class MigrationRaceTests : SquashTestBase
{
    private static readonly (int Version, string Name)[] Baseline = [(40, BaselineName)];

    private static readonly List<(int Version, string Name)> Expected =
    [
        (40, BaselineName),
        (41, "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql"),
        (42, "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql"),
    ];

    /// <summary>A database of the current schema whose ledger stops where a database of release 2.6.x or a node that was killed after the baseline does.</summary>
    private async Task<string> TemplateAsync(string name, IEnumerable<(int Version, string Name)> ledger)
    {
        var path = await CurrentSchemaDbAsync(NewDbPath(name));
        SetLedger(path, ledger);
        return path;
    }

    /// <summary>The whole ledger (SchemaDump.Ledger stops at the baseline).</summary>
    private static List<(int Version, string Name)> FullLedger(string path)
    {
        List<(int, string)> rows;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            rows = conn.Query<(int, string)>("SELECT version, filename FROM tbl_migration ORDER BY version").ToList();
        SqliteConnection.ClearAllPools();
        return rows;
    }

    private string CopyOf(string template, string name)
    {
        var path = NewDbPath(name);
        File.Copy(template, path);
        return path;
    }

    // The runners of one test live in one process and so share the provider's pool for the file; a factory that is disposed clears it under the
    // others. Two programs never share a pool, so the factories are all kept until every runner is done.
    private static Task RunAsync(DbConnectionFactory factory, Action<int>? beforeTransaction) =>
        Task.Run(() => new MigrationRunner(factory) { BeforeTransaction = beforeTransaction }.RunMigrationsAsync());

    /// <summary>The runners are held at the gap between "I read the ledger" and "I open my transaction" until all of them are there.</summary>
    private static async Task RunTogetherAsync(string path, int runners)
    {
        var gates = new Dictionary<int, Barrier> { [41] = new(runners), [42] = new(runners) };
        void Gate(int version)
        {
            if (gates.TryGetValue(version, out var gate)) gate.SignalAndWait(TimeSpan.FromSeconds(20));
        }

        var factories = Enumerable.Range(0, runners).Select(_ => new DbConnectionFactory(path)).ToList();
        try { await Task.WhenAll(factories.Select(f => RunAsync(f, Gate))); }
        finally { factories.ForEach(f => f.Dispose()); }
        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Runners_that_all_decided_before_the_first_commit_that_041_and_042_are_missing_all_succeed(int runners)
    {
        var template = await TemplateAsync("template.db", Baseline);
        for (var round = 0; round < 5; round++)
        {
            var path = CopyOf(template, $"gap{runners}_{round}.db");
            await RunTogetherAsync(path, runners);
            FullLedger(path).Should().Equal(Expected, $"round {round}: each migration is recorded once");
        }
    }

    [Fact]
    public async Task Runners_held_at_the_gap_on_a_2_6_database_succeed_and_leave_the_ledger_of_this_release()
    {
        var template = await TemplateAsync("template26.db", LegacyChain.LedgerOf("v2.6.1"));
        for (var round = 0; round < 5; round++)
        {
            var path = CopyOf(template, $"gap26_{round}.db");
            await RunTogetherAsync(path, 2);
            FullLedger(path).Should().Equal(Expected, $"round {round}");
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Runners_started_together_many_times_all_succeed(int runners)
    {
        // No hook: the natural timing, as CI saw it. The seam test above holds the window open on purpose; this one keeps the code honest without it.
        var templates = new[]
        {
            await TemplateAsync($"tpl_stress_a{runners}.db", Baseline),
            await TemplateAsync($"tpl_stress_b{runners}.db", LegacyChain.LedgerOf("v2.6.1")),
        };
        const int iterations = 120;
        var failures = new List<string>();
        for (var i = 0; i < iterations; i++)
        {
            var path = CopyOf(templates[i % 2], $"stress{runners}_{i}.db");
            var start = new ManualResetEventSlim(false);
            var factories = Enumerable.Range(0, runners).Select(_ => new DbConnectionFactory(path)).ToList();
            var runs = factories.Select(f => Task.Run(() =>
            {
                start.Wait();
                return new MigrationRunner(f).RunMigrationsAsync();
            })).ToList();
            start.Set();
            try { await Task.WhenAll(runs); }
            catch (SqliteException ex) { failures.Add($"iteration {i}: {ex.Message}"); }
            finally { factories.ForEach(f => f.Dispose()); }
            SqliteConnection.ClearAllPools();
            if (!FullLedger(path).SequenceEqual(Expected)) failures.Add($"iteration {i}: the ledger is not 40, 41, 42");
        }
        Assert.True(failures.Count == 0, $"every runner of every one of the {iterations} iterations must start; {failures.Count} iterations did not: {string.Join(" | ", failures.Take(5))}");
    }
}

using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314, the adoption matrix on REAL old databases: each is built by the frozen runner over the files that release shipped (so it has
/// exactly the objects, the column order and the ledger a node of that release had), filled with synthetic rows in every table, closed,
/// and then handed to the runner of today. 2.6.0 and 2.6.1 are adopted (schema and every row kept, the ledger collapsed to the baseline
/// row); everything older is refused with the file unchanged. The files come from the git history (see <see cref="LegacyChain"/>); a clone
/// without that history skips these tests.
/// </summary>
public class ReleasedVersionsTests(ITestOutputHelper output) : SquashTestBase
{
    private async Task<string> BuildAsync(string name, params string[] tags)
    {
        var path = NewDbPath(name);
        using (var factory = new DbConnectionFactory(path))
            foreach (var tag in tags)
            {
                await LegacyChain.ApplyAsync(factory, tag);
                using var conn = factory.CreateConnection();
                SyntheticRows.Seed(conn); // between the hops too: an update runs over a database that has data in it
            }
        SqliteConnection.ClearAllPools();
        return path;
    }

    [LegacyChainTheory]
    [InlineData("v2.1.0")]
    [InlineData("v2.2.0")]
    [InlineData("v2.3.0")]
    [InlineData("v2.4.0")]
    [InlineData("v2.5.0")]
    public async Task A_database_of_a_release_before_2_6_is_refused_and_the_file_is_untouched(string tag)
    {
        var path = await BuildAsync("old.db", tag);
        Query<long>(path, "SELECT COUNT(*) FROM tbl_migration").Should().Be(LegacyChain.Chains[tag].Length);

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain("older than 2.6").And.Contain("2.6.1");
    }

    [LegacyChainTheory]
    [InlineData("v2.6.0")]
    [InlineData("v2.6.1")]
    public async Task A_database_of_2_6_is_adopted_with_its_schema_and_every_row(string tag)
    {
        var path = await BuildAsync("old.db", tag);
        var before = ReadAll(path);
        before.Ledger.Should().HaveCount(39);
        var seeded = RowCounts(path);
        seeded.Where(t => t.Value > 0).Should().HaveCountGreaterThan(40, "the synthetic rows reached most tables");

        await RunNewRunnerAsync(path);

        var after = ReadAll(path);
        after.Ledger.Should().Equal([(40, BaselineName)]);
        after.Shape.Should().Be(before.Shape);
        after.Rows.Should().Be(before.Rows);
        Query<string>(path, "PRAGMA integrity_check").Should().Be("ok");
        Run(path, "INSERT INTO fts_article(fts_article) VALUES ('integrity-check')");
    }

    public static IEnumerable<object[]> Hops() =>
    [
        ["v2.2.0,v2.6.1"],
        ["v2.3.0,v2.4.0,v2.6.0"],
        ["v2.5.0,v2.6.1"],
        ["v2.1.0,v2.2.0,v2.3.0,v2.4.0,v2.5.0,v2.6.0,v2.6.1"],
        ["v2.2.0,v2.3.0,v2.4.0,v2.5.0,v2.6.0,v2.6.1"],
    ];

    [LegacyChainTheory]
    [MemberData(nameof(Hops))]
    public async Task A_database_that_hopped_through_releases_is_adopted_like_any_other(string hops)
    {
        var path = await BuildAsync("hopped.db", hops.Split(','));
        var before = ReadAll(path);
        before.Ledger.Should().HaveCount(39, "the last release's chain is complete however the database got there");

        await RunNewRunnerAsync(path);

        var after = ReadAll(path);
        after.Ledger.Should().Equal([(40, BaselineName)]);
        after.Shape.Should().Be(before.Shape, "adoption left every object as the hops made it");
        after.Rows.Should().Be(before.Rows);
        Query<string>(path, "PRAGMA integrity_check").Should().Be("ok");
    }

    [LegacyChainFact]
    public async Task A_database_that_hopped_has_the_shape_of_one_that_did_not()
    {
        // If this fails the adoption still has to cope (the test above), but the baseline would not describe every real database exactly.
        var hopped = await BuildAsync("hopped.db", "v2.2.0", "v2.3.0", "v2.4.0", "v2.5.0", "v2.6.0", "v2.6.1");
        var straight = await BuildAsync("straight.db", "v2.6.1");

        string a, b;
        // The AUTOINCREMENT counters are left out: the hopped database was seeded after every hop, so its counters are higher by construction.
        using (var f = new DbConnectionFactory(hopped)) using (var c = f.CreateConnection()) a = SchemaDump.Shape(c, withLedger: false, withSequences: false);
        using (var f = new DbConnectionFactory(straight)) using (var c = f.CreateConnection()) b = SchemaDump.Shape(c, withLedger: false, withSequences: false);
        SqliteConnection.ClearAllPools();

        if (a != b)
        {
            var la = a.Split('\n'); var lb = b.Split('\n');
            output.WriteLine("hopped-only: " + string.Join(" || ", la.Except(lb)));
            output.WriteLine("straight-only: " + string.Join(" || ", lb.Except(la)));
        }
        a.Should().Be(b);
    }

    [LegacyChainFact]
    public async Task A_sealed_2_6_1_database_built_by_the_old_chain_is_adopted_through_the_sealed_connection()
    {
        var path = NewDbPath();
        var keys = new InMemoryVaultKeySource();
        using (var factory = new DbConnectionFactory(path, keys))
        {
            await LegacyChain.ApplyAsync(factory, "v2.6.1");
            using var conn = factory.CreateConnection();
            SyntheticRows.Seed(conn);
        }
        SqliteConnection.ClearAllPools();
        (File.ReadAllBytes(path).Take(16).SequenceEqual("SQLite format 3\0"u8.ToArray())).Should().BeFalse("the file is encrypted");

        string shape, rows;
        using (var factory = new DbConnectionFactory(path, keys))
        using (var conn = factory.CreateConnection()) { shape = SchemaDump.Shape(conn, withLedger: false); rows = SchemaDump.RowsText(conn); }
        SqliteConnection.ClearAllPools();

        using (var factory = new DbConnectionFactory(path, keys))
            await Runner(factory).RunMigrationsAsync();
        SqliteConnection.ClearAllPools();

        using (var factory = new DbConnectionFactory(path, keys))
        using (var conn = factory.CreateConnection())
        {
            SchemaDump.Ledger(conn).Should().Equal([(40, BaselineName)]);
            SchemaDump.Shape(conn, withLedger: false).Should().Be(shape);
            SchemaDump.RowsText(conn).Should().Be(rows);
        }
        SqliteConnection.ClearAllPools();
    }

    [LegacyChainFact]
    public async Task A_sealed_2_5_database_is_refused_through_the_sealed_connection()
    {
        var path = NewDbPath();
        var keys = new InMemoryVaultKeySource();
        using (var factory = new DbConnectionFactory(path, keys))
            await LegacyChain.ApplyAsync(factory, "v2.5.0");
        SqliteConnection.ClearAllPools();
        var before = SchemaDump.FileDigest(path);

        using (var factory = new DbConnectionFactory(path, keys))
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Runner(factory).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();

        SchemaDump.FileDigest(path).Should().Be(before);
    }

    [LegacyChainFact]
    public async Task The_runner_of_2_6_1_refuses_an_adopted_database_before_it_writes_anything_and_the_new_one_takes_it_again()
    {
        // An update, then the old program back on the same files (a rolled-back image). Without the guard trigger the old runner deletes
        // the baseline row as a ghost, re-runs its data migrations over the live data (012, 014, 015) and dies at 016 ("no such column:
        // ciphertext") with the ledger half rewritten: after that neither program would take the database. With it, the DELETE of the ledger
        // row is the first thing it tries and the trigger stops it, with a message that says what to do.
        var path = await BuildAsync("rollback.db", "v2.6.1");
        await RunNewRunnerAsync(path);
        SqliteConnection.ClearAllPools();
        var before = SchemaDump.FileDigest(path);

        SqliteException thrown;
        using (var factory = new DbConnectionFactory(path))
            thrown = await Assert.ThrowsAsync<SqliteException>(() => new LegacyMigrationRunner(factory, LegacyChain.FilesOf("v2.6.1")).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();

        output.WriteLine("the old runner said: " + thrown.Message);
        thrown.Message.Should().Contain("version 2.6.2 or later").And.Contain("Update the program");
        SchemaDump.FileDigest(path).Should().Be(before, "the old runner changed nothing");

        await RunNewRunnerAsync(path);
        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)], "the current program opens it as if nothing had happened");
    }

    // ---- no old files needed: a real database written by the 2.5.1 tree ----------------------------------------------------------

    [Fact]
    public async Task The_real_2_5_1_database_is_refused_and_the_file_is_untouched()
    {
        var path = NewDbPath();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "plain-2.5.1.sqlite"), path);

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain("older than 2.6").And.Contain("2.6.1");
    }

    private static SortedDictionary<string, long> RowCounts(string path)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            foreach (var (table, (count, _)) in SchemaDump.Rows(conn)) result[table] = count;
        SqliteConnection.ClearAllPools();
        return result;
    }
}

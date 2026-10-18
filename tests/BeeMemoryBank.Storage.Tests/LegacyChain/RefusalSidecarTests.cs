using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314 fix S1: a database that is refused is not written to, in the widest sense: not its main file, not its header (the journal mode
/// lives in two header bytes), and no <c>-wal</c>, <c>-shm</c> or <c>-journal</c> sidecar appears next to it. The runner used to open its normal
/// connection, whose initialization runs <c>PRAGMA journal_mode=WAL</c>, before it had looked at the ledger; a database in rollback-journal
/// mode was converted, and sidecars could appear, before the refusal. The ledger is now read through a read-only connection with no
/// initialization, and the normal connection is opened only for a database the runner accepts.
/// </summary>
public class RefusalSidecarTests : SquashTestBase
{
    /// <summary>The database file and every file next to it that belongs to it: name, size and digest, so an extra sidecar shows.</summary>
    private static List<string> Footprint(string dbPath) =>
        Directory.GetFiles(Path.GetDirectoryName(dbPath)!, Path.GetFileName(dbPath) + "*")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} {SchemaDump.FileDigest(f)}")
            .ToList();

    /// <summary>A 2.6.x-shaped database in the journal mode asked for, closed cleanly: one file when it is a rollback journal, the main file in WAL mode.</summary>
    private async Task<string> DatabaseAsync(bool rollbackJournal, IEnumerable<(int, string)> ledger, string? arrangeSql = null)
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        SetLedger(path, ledger);
        if (arrangeSql is not null) Run(path, arrangeSql);
        SqliteConnection.ClearAllPools();
        if (rollbackJournal)
        {
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=DELETE";
                cmd.ExecuteScalar().Should().Be("delete");
            }
            SqliteConnection.ClearAllPools();
            File.ReadAllBytes(path).Skip(18).Take(2).Should().Equal(new byte[] { 1, 1 }, "the header says rollback journal");
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static async Task<InvalidOperationException> RefusedWithoutAnyTraceAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        var before = Footprint(path);
        InvalidOperationException ex;
        using (var factory = new DbConnectionFactory(path))
            ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new MigrationRunner(factory).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();
        Footprint(path).Should().Equal(before, "the main file, its header and its sidecars are as they were, and no sidecar has appeared");
        return ex;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_database_of_a_release_before_2_6_is_refused_without_a_trace(bool rollbackJournal)
    {
        var path = await DatabaseAsync(rollbackJournal, LegacyChain.LedgerOf("v2.5.0"));

        var ex = await RefusedWithoutAnyTraceAsync(path);

        ex.Message.Should().Contain("older than 2.6");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ledger_nobody_knows_is_refused_without_a_trace(bool rollbackJournal)
    {
        var path = await DatabaseAsync(rollbackJournal, LegacyChain.LedgerOf("v2.6.1").Append((41, "BeeMemoryBank.Storage.Migrations.041_later.sql")));

        await RefusedWithoutAnyTraceAsync(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tables_without_a_ledger_row_are_refused_without_a_trace(bool rollbackJournal)
    {
        var path = await DatabaseAsync(rollbackJournal, []);

        await RefusedWithoutAnyTraceAsync(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_2_6_database_with_a_missing_column_is_refused_without_a_trace(bool rollbackJournal)
    {
        var path = await DatabaseAsync(rollbackJournal, LegacyChain.LedgerOf("v2.6.1"), "ALTER TABLE tbl_node_identity DROP COLUMN brand_name");

        var ex = await RefusedWithoutAnyTraceAsync(path);

        ex.Message.Should().Contain("brand_name");
    }

    [Fact]
    public async Task A_sealed_database_of_a_release_before_2_6_is_refused_without_a_trace()
    {
        var path = NewDbPath();
        var keys = new BeeMemoryBank.Storage.Sealed.InMemoryVaultKeySource();
        using (var factory = new DbConnectionFactory(path, keys))
        {
            await new MigrationRunner(factory).RunMigrationsAsync();
            using var conn = factory.CreateConnection();
            SetLedger(conn, LegacyChain.LedgerOf("v2.5.0"));
        }
        SqliteConnection.ClearAllPools();
        var before = Footprint(path);

        using (var factory = new DbConnectionFactory(path, keys))
            await Assert.ThrowsAsync<MigrationRefusedException>(() => new MigrationRunner(factory).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();

        Footprint(path).Should().Equal(before, "the sealed file and its sidecars are as they were");
    }

    [Fact]
    public async Task A_rollback_journal_database_that_is_accepted_is_opened_normally_afterwards()
    {
        // The other side of the same rule: an accepted database still gets the normal connection (WAL and the rest) once it is accepted.
        var path = await DatabaseAsync(rollbackJournal: true, LegacyChain.LedgerOf("v2.6.1"));

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        File.ReadAllBytes(path).Skip(18).Take(2).Should().Equal(new byte[] { 2, 2 }, "an accepted database is switched to WAL like every database of this program");
    }

    [Fact]
    public async Task A_database_that_does_not_exist_yet_is_created()
    {
        var path = NewDbPath();

        await RunNewRunnerAsync(path);

        File.Exists(path).Should().BeTrue();
        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }

    [Fact]
    public async Task An_empty_file_is_a_new_database()
    {
        var path = NewDbPath();
        File.WriteAllBytes(path, []);

        await RunNewRunnerAsync(path);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }
}

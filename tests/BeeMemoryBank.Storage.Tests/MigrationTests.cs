using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The runner on a new database. What it does with databases that already have a ledger (adoption of 2.6.x, refusal of older ones) is in
/// <c>Squash/AdoptionTests</c> and <c>Squash/ReleasedVersionsTests</c>; the old tests of single migrations (012, 014, 015, 017, 035: data
/// fixes and the blob-store contraction) went with the migration files they ran, which the baseline replaced.
/// </summary>
public class MigrationTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private MigrationRunner _runner = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_test_{Guid.NewGuid():N}");
        _runner = new MigrationRunner(_factory);
        await _runner.RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunMigrations_AppliesWithoutErrors()
    {
        using var conn = _factory.CreateConnection();
        var version = await conn.QuerySingleAsync<int>("SELECT MAX(version) FROM tbl_migration");
        version.Should().Be(MigrationRunner.BaselineVersion, "the baseline is the schema of migration 40; the next migration is 41");
    }

    [Fact]
    public async Task RunMigrations_IsIdempotent()
    {
        // repeated run should not throw exceptions
        await _runner.RunMigrationsAsync();

        using var conn = _factory.CreateConnection();
        var count = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_migration");
        count.Should().Be(1);
    }

    [Fact]
    public async Task Schema_AllTablesExist()
    {
        using var conn = _factory.CreateConnection();
        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        tables.Should().Contain("tbl_article");
        tables.Should().Contain("tbl_article_body");
        tables.Should().Contain("tbl_article_concept_tag");
        tables.Should().Contain("tbl_audit_log");
        tables.Should().Contain("tbl_conflict_version");
        tables.Should().Contain("tbl_concept_tag");
        tables.Should().Contain("tbl_folder_acl_entry");
        tables.Should().Contain("tbl_key_slot");
        tables.Should().Contain("tbl_migration");
        tables.Should().Contain("tbl_node_identity");
        tables.Should().Contain("tbl_node_data_key");
        tables.Should().Contain("tbl_whitelist");
        tables.Should().Contain("tbl_projection_matrix");

        tables.Should().NotContain("tbl_tag");
        tables.Should().NotContain("tbl_article_tag");
        tables.Should().NotContain("tbl_folder_restriction");
    }

    [Fact]
    public async Task The_downgrade_guard_stops_every_delete_from_the_ledger()
    {
        // The runner of 2.6.1 and older deletes a ledger row it does not know before it does anything else; the guard makes that fail,
        // with a message that says what to do, before it has changed anything (Squash/ReleasedVersionsTests runs the real old runner).
        using var conn = _factory.CreateConnection();

        var act = () => conn.ExecuteAsync("DELETE FROM tbl_migration");

        (await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>()).Which.Message
            .Should().Contain("2.6.2 or later").And.Contain("Update the program");
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_migration")).Should().Be(1);
    }
}

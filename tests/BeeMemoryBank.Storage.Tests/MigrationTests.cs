using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

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
        version.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task RunMigrations_IsIdempotent()
    {
        // repeated run should not throw exceptions
        await _runner.RunMigrationsAsync();

        using var conn = _factory.CreateConnection();
        var count = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_migration");
        count.Should().BeGreaterThanOrEqualTo(1);
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
        tables.Should().Contain("tbl_whitelist");
        tables.Should().Contain("tbl_projection_matrix");

        tables.Should().NotContain("tbl_tag");
        tables.Should().NotContain("tbl_article_tag");
        tables.Should().NotContain("tbl_folder_restriction");
    }
}

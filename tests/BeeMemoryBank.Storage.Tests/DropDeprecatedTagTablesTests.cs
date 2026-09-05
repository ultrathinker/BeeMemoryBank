using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Migration 024 drops the dead keyword-tag tables (tbl_tag / tbl_article_tag_deprecated) that a
/// pre-squash database still carries. A fresh chain never creates them, so the migration runner's
/// own run proves the no-op case (all Storage tests init a fresh DB and pass). This covers the
/// other half — that the DROP statements actually remove the tables when they DO exist, and that a
/// current, live table (tbl_concept_tag) is not caught by them.
/// </summary>
public class DropDeprecatedTagTablesTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    // The body of migration 024, verbatim.
    private const string DropSql = @"
        DROP TABLE IF EXISTS tbl_article_tag_deprecated;
        DROP TABLE IF EXISTS tbl_tag;";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_droptags_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<bool> TableExistsAsync(string name)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name", new { name }) > 0;
    }

    [Fact]
    public async Task Drop_RemovesTheDeadTables_WhenTheyExist_AndLeavesTheLiveConceptTagTable()
    {
        // Recreate the pre-squash shape the production DB still has.
        using (var conn = _factory.CreateConnection())
        {
            await conn.ExecuteAsync("CREATE TABLE tbl_tag (id INTEGER PRIMARY KEY, name TEXT)");
            await conn.ExecuteAsync(
                @"CREATE TABLE tbl_article_tag_deprecated (
                    article_id TEXT, tag_id INTEGER REFERENCES tbl_tag(id))");
            await conn.ExecuteAsync("INSERT INTO tbl_tag (name) VALUES ('legacy')");
            await conn.ExecuteAsync("INSERT INTO tbl_article_tag_deprecated (article_id, tag_id) VALUES ('x', 1)");
        }

        (await TableExistsAsync("tbl_tag")).Should().BeTrue();
        (await TableExistsAsync("tbl_article_tag_deprecated")).Should().BeTrue();

        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync(DropSql);

        (await TableExistsAsync("tbl_tag")).Should().BeFalse();
        (await TableExistsAsync("tbl_article_tag_deprecated")).Should().BeFalse();
        (await TableExistsAsync("tbl_concept_tag")).Should().BeTrue("the live concept-tag table is untouched");
    }

    [Fact]
    public async Task Drop_IsANoOp_WhenTheTablesAreAbsent()
    {
        // The fresh-chain case: the tables never existed. IF EXISTS must not throw.
        using var conn = _factory.CreateConnection();
        var act = async () => await conn.ExecuteAsync(DropSql);
        await act.Should().NotThrowAsync();
    }
}

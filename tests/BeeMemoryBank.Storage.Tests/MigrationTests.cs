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

    // Regression test for finding C1 / migration 012: any recovery slot created before the fix
    // has its Argon2id salt column set to the recovery key's own raw bytes — an unrecoverable
    // exposure that can only be closed by invalidating the slot outright (see the migration
    // file's own comment for the full rationale). This simulates an existing vault that already
    // had a (now-unsafe) recovery slot when it upgrades past this migration.
    [Fact]
    public async Task Migration012_RemovesExistingRecoverySlots_ButLeavesOtherSlotTypesAlone()
    {
        using (var conn = _factory.CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_key_slot
                  (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                  VALUES ('recovery', @dek, @iv, @salt, 65536, 3, 4, @now)",
                new { dek = new byte[] { 1, 2, 3 }, iv = new byte[] { 4, 5, 6 }, salt = new byte[] { 7, 8, 9 }, now });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_key_slot
                  (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                  VALUES ('user', @dek, @iv, @salt, 65536, 3, 4, @now)",
                new { dek = new byte[] { 10, 11, 12 }, iv = new byte[] { 13, 14, 15 }, salt = new byte[] { 16, 17, 18 }, now });

            // Force migration 012 to be re-applied, simulating a vault that had already run every
            // migration UP TO this one — its recovery slot (inserted above) predates the fix and
            // must be swept up the same way an upgrading production node's would be.
            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 12");
        }

        await _runner.RunMigrationsAsync();

        using var check = _factory.CreateConnection();
        var slotTypes = (await check.QueryAsync<string>("SELECT slot_type FROM tbl_key_slot")).ToList();
        slotTypes.Should().NotContain("recovery", "an unsafe pre-fix recovery slot must be removed, not silently kept around");
        slotTypes.Should().Contain("user", "only recovery slots are unsafe — password/user slots must survive untouched");
    }
}

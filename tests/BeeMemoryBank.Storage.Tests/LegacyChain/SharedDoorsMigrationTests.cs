using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Squash;

public sealed class SharedDoorsMigrationTests : SquashTestBase
{
    [Fact]
    public void Migration_050_is_registered_as_additive()
    {
        MigrationRunner.AdditiveMigrations.Should().Contain(50);
    }

    [Fact]
    public async Task New_database_has_050_schema_and_replicated_defaults_to_zero()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        DapperConfig.Configure();
        using var factory = new DbConnectionFactory(path);
        using var conn = factory.CreateConnection();

        var userCols = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_user')")).ToList();
        userCols.Should().Contain("replicated");

        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'")).ToList();
        tables.Should().Contain(["tbl_accounts_home", "tbl_shared_doors_state"]);

        SnapshotTables.Replicated.Should().Contain("tbl_accounts_home");
        SnapshotTables.Replicated.Should().NotContain("tbl_shared_doors_state");
    }

    [Fact]
    public async Task Upgrade_from_047_preserves_existing_users_with_replicated_zero()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path,
            "DROP TABLE tbl_accounts_home; DROP TABLE tbl_shared_doors_state; " +
            "ALTER TABLE tbl_user DROP COLUMN replicated; " +
            "INSERT INTO tbl_user (person_id, username, display_name, password_hash, role, is_active, created_at, security_stamp, chat_access) " +
            "VALUES ('11111111-1111-4111-8111-111111111111', 'alice', 'Alice', 'hash', 'superadmin', 1, '2026-11-16T00:00:00Z', 'stamp', 1);");
        SetLedger(path,
        [
            (40, BaselineName),
            (41, "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql"),
            (42, "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql"),
            (43, "BeeMemoryBank.Storage.Migrations.043_password_hint.sql"),
            (44, "BeeMemoryBank.Storage.Migrations.044_peer_channels.sql"),
            (45, "BeeMemoryBank.Storage.Migrations.045_whitelist_concern_versions.sql"),
            (46, "BeeMemoryBank.Storage.Migrations.046_sealed_doors.sql"),
            (47, "BeeMemoryBank.Storage.Migrations.047_ownership_and_node_state.sql"),
        ]);

        await RunNewRunnerAsync(path);

        DapperConfig.Configure();
        using var factory = new DbConnectionFactory(path);
        var users = new UserRepository(factory);
        var alice = await users.GetByUsernameAsync("alice");
        alice.Should().NotBeNull();
        alice!.Replicated.Should().BeFalse();
    }
}

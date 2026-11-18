using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// Migration 046 (sealed doors, docs/adr/0012-sealed-doors.md): on a database of 045 every door stays a v1 door with an unknown DEK, every
/// <c>user</c> slot gets the person tbl_user points at as its owner, a recovery slot stays without one; from then on the database keeps the owner
/// of a user door itself (two triggers on tbl_user). Run again, it changes nothing.
/// </summary>
public class SealedDoorsMigrationTests : SquashTestBase
{
    private static readonly (int, string)[] LedgerOf045 =
    [
        (40, BaselineName), (41, "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql"),
        (42, "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql"), (43, "BeeMemoryBank.Storage.Migrations.043_password_hint.sql"),
        (44, "BeeMemoryBank.Storage.Migrations.044_peer_channels.sql"), (45, "BeeMemoryBank.Storage.Migrations.045_whitelist_concern_versions.sql"),
    ];

    private static readonly string[] SlotColumns = ["door_version", "door_public_key", "dek_fp", "owner_user_id", "created_by_user_id", "label"];
    private static readonly string[] AgentColumns = ["door_public_key", "dek_fp", "created_by_user_id"];

    [Fact]
    public async Task A_new_database_has_the_door_columns_and_the_migration_is_additive()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        foreach (var column in SlotColumns)
            Query<long>(path, $"SELECT COUNT(*) FROM pragma_table_info('tbl_key_slot') WHERE name = '{column}'").Should().Be(1, column);
        foreach (var column in AgentColumns)
            Query<long>(path, $"SELECT COUNT(*) FROM pragma_table_info('tbl_agent') WHERE name = '{column}'").Should().Be(1, column);
        Query<string>(path, "SELECT type || ',' || \"notnull\" || ',' || dflt_value FROM pragma_table_info('tbl_key_slot') WHERE name = 'door_version'")
            .Should().Be("INTEGER,1,1");
        MigrationRunner.AdditiveMigrations.Should().Contain(46, "the migration only adds columns and triggers");
    }

    [Fact]
    public async Task A_database_of_045_keeps_every_door_as_v1_and_gets_the_owners_of_user_slots()
    {
        var path = await DatabaseOf045Async();
        Run(path, @"
            INSERT INTO tbl_key_slot (slot_id, slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
            VALUES (1, 'user', x'0101', x'01', x'01', 65536, 3, 4, '2026-01-01T00:00:00Z'),
                   (2, 'recovery', x'0202', x'02', x'02', 65536, 3, 4, '2026-01-01T00:00:00Z'),
                   (3, 'user', x'0303', x'03', x'03', 65536, 3, 4, '2026-01-01T00:00:00Z'),
                   (4, 'os_auto_unlock', x'0404', x'04', NULL, NULL, NULL, NULL, '2026-01-01T00:00:00Z');
            INSERT INTO tbl_user (id, username, display_name, password_hash, role, key_slot_id, created_at)
            VALUES (7, 'ann', 'Ann', 'h', 'superadmin', 1, '2026-01-01T00:00:00Z'),
                   (8, 'bob', 'Bob', 'h', 'user', NULL, '2026-01-01T00:00:00Z');");

        await RunNewRunnerAsync(path);

        Query<string>(path, "SELECT group_concat(slot_id || ':' || door_version || ':' || ifnull(owner_user_id, '-') || ':' || ifnull(dek_fp, '-') || ':' || ifnull(door_public_key, '-'), ',') FROM (SELECT * FROM tbl_key_slot ORDER BY slot_id)")
            .Should().Be("1:1:7:-:-,2:1:-:-:-,3:1:-:-:-,4:1:-:-:-",
                "every door stays v1 with an unknown DEK; a user slot gets the person who points at it; a recovery key stays without an owner; nobody points at slot 3");
    }

    [Fact]
    public async Task After_it_the_database_keeps_the_owner_of_a_user_door_itself()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, @"
            INSERT INTO tbl_key_slot (slot_id, slot_type, encrypted_master_dek, iv, created_at) VALUES (1, 'user', x'01', x'01', 'x'), (2, 'user', x'02', x'02', 'x'),
                (3, 'recovery', x'03', x'03', 'x');
            INSERT INTO tbl_user (id, username, display_name, password_hash, role, key_slot_id, created_at) VALUES (5, 'ann', 'Ann', 'h', 'superadmin', 1, 'x');");
        Query<long>(path, "SELECT owner_user_id FROM tbl_key_slot WHERE slot_id = 1").Should().Be(5, "an insert that points at a slot owns it");

        Run(path, "UPDATE tbl_user SET key_slot_id = 2 WHERE id = 5");
        Query<long>(path, "SELECT owner_user_id FROM tbl_key_slot WHERE slot_id = 2").Should().Be(5, "a repoint (a password change) owns the new slot");

        Run(path, "UPDATE tbl_user SET key_slot_id = 3 WHERE id = 5");
        Query<long?>(path, "SELECT owner_user_id FROM tbl_key_slot WHERE slot_id = 3").Should().BeNull("only a user slot gets an owner this way");
    }

    [Fact]
    public async Task Run_again_it_changes_nothing()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, @"
            INSERT INTO tbl_key_slot (slot_id, slot_type, encrypted_master_dek, iv, created_at, door_version, door_public_key, dek_fp, owner_user_id)
            VALUES (1, 'user', x'01', x'01', 'x', 2, x'aa', 'fp', 9);
            INSERT INTO tbl_user (id, username, display_name, password_hash, role, key_slot_id, created_at) VALUES (9, 'ann', 'Ann', 'h', 'superadmin', 1, 'x');");
        SetLedger(path, LedgerOf045);

        await RunNewRunnerAsync(path);

        Query<string>(path, "SELECT door_version || ':' || hex(door_public_key) || ':' || dek_fp || ':' || owner_user_id FROM tbl_key_slot WHERE slot_id = 1")
            .Should().Be("2:AA:fp:9");
    }

    /// <summary>The current schema with the door columns and triggers taken away and the ledger of a node that ran 045.</summary>
    private async Task<string> DatabaseOf045Async()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, "DROP TRIGGER trg_key_slot_owner_user_insert; DROP TRIGGER trg_key_slot_owner_user_update; "
            + string.Join("; ", SlotColumns.Select(c => $"ALTER TABLE tbl_key_slot DROP COLUMN {c}")) + "; "
            + string.Join("; ", AgentColumns.Select(c => $"ALTER TABLE tbl_agent DROP COLUMN {c}")));
        SetLedger(path, LedgerOf045);
        return path;
    }
}

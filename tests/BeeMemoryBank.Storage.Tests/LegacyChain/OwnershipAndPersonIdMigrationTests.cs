using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// Migration 047: <c>tbl_user.person_id</c>, ownership columns + backfill on <c>tbl_whitelist</c>,
/// initial ownership columns on <c>tbl_blind_invite</c>, and <c>tbl_node_state</c> (which travels in snapshots).
/// </summary>
public class OwnershipAndPersonIdMigrationTests : SquashTestBase
{
    private static readonly (int, string)[] LedgerOf045 =
    [
        (40, BaselineName),
        (41, "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql"),
        (42, "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql"),
        (43, "BeeMemoryBank.Storage.Migrations.043_password_hint.sql"),
        (44, "BeeMemoryBank.Storage.Migrations.044_peer_channels.sql"),
        (45, "BeeMemoryBank.Storage.Migrations.045_whitelist_concern_versions.sql"),
    ];

    [Fact]
    public async Task New_database_has_person_id_ownership_and_node_state_and_47_is_additive()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        MigrationRunner.AdditiveMigrations.Should().Contain(47);
        SnapshotTables.Replicated.Should().Contain("tbl_node_state");

        Query<string>(path, "SELECT group_concat(name) FROM pragma_table_info('tbl_node_state') ORDER BY cid")
            .Should().Be("node_id,app_version,platform,runtime_kind,identity_key_fp,dek_fp,attention_flags_json,extra_json,lamport_ts,event_id,updated_at");
        Query<long>(path, "SELECT COUNT(*) FROM pragma_table_info('tbl_user') WHERE name = 'person_id' AND type = 'TEXT' AND \"notnull\" = 1")
            .Should().Be(1);
    }

    [Fact]
    public async Task Upgrade_from_045_backfills_person_id_and_whitelist_ownership()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, @"
            DROP TABLE tbl_node_state;
            DROP TRIGGER trg_migration_ledger_guard;
            DELETE FROM tbl_migration WHERE version = 47;
            ALTER TABLE tbl_user DROP COLUMN person_id;
            ALTER TABLE tbl_whitelist DROP COLUMN ownership;
            ALTER TABLE tbl_whitelist DROP COLUMN owner_person_id;
            ALTER TABLE tbl_whitelist DROP COLUMN owner_label;
            ALTER TABLE tbl_whitelist DROP COLUMN ownership_lamport_ts;
            ALTER TABLE tbl_whitelist DROP COLUMN ownership_source_node_id;
            ALTER TABLE tbl_whitelist DROP COLUMN needs_review;
            ALTER TABLE tbl_blind_invite DROP COLUMN ownership;
            ALTER TABLE tbl_blind_invite DROP COLUMN owner_person_id;
            ALTER TABLE tbl_blind_invite DROP COLUMN owner_label;");
        SetLedger(path, LedgerOf045);

        var blindId = BlindNodeId.NewId().ToString("D");
        var peerId = Guid.NewGuid().ToString("D");
        var unconfirmedId = Guid.NewGuid().ToString("D");
        var revokedId = Guid.NewGuid().ToString("D");
        Run(path, $@"
            INSERT INTO tbl_user (username, display_name, password_hash, role, created_at)
            VALUES ('alice', 'Alice', 'h1', 'superadmin', '2026-01-01T00:00:00Z'),
                   ('bob', 'Bob', 'h2', 'user', '2026-01-01T00:00:00Z');
            INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, created_at, updated_at)
            VALUES ('{blindId}', 'Blind Storage', x'01', 'A', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                   ('{peerId}', 'Alice Laptop', x'02', 'A', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                   ('{unconfirmedId}', 'Restored Desktop', x'04', 'U', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                   ('{revokedId}', 'Old Phone', x'03', 'R', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");

        await RunNewRunnerAsync(path);

        var alicePerson = Query<string>(path, "SELECT person_id FROM tbl_user WHERE username = 'alice'");
        var bobPerson = Query<string>(path, "SELECT person_id FROM tbl_user WHERE username = 'bob'");
        Guid.TryParse(alicePerson, out var g1).Should().BeTrue();
        Guid.TryParse(bobPerson, out var g2).Should().BeTrue();
        g1.Should().NotBe(Guid.Empty);
        g2.Should().NotBe(Guid.Empty);
        g1.Should().NotBe(g2);

        Query<string>(path, $"SELECT ownership || ':' || needs_review || ':' || ownership_lamport_ts FROM tbl_whitelist WHERE node_id = '{blindId}'")
            .Should().Be("shared:0:0");
        Query<string>(path, $"SELECT ownership || ':' || needs_review || ':' || ownership_lamport_ts FROM tbl_whitelist WHERE node_id = '{peerId}'")
            .Should().Be("unknown:1:0");
        Query<string>(path, $"SELECT ownership || ':' || needs_review || ':' || ownership_lamport_ts FROM tbl_whitelist WHERE node_id = '{unconfirmedId}'")
            .Should().Be("unknown:1:0");
        Query<string>(path, $"SELECT ownership || ':' || needs_review || ':' || ownership_lamport_ts FROM tbl_whitelist WHERE node_id = '{revokedId}'")
            .Should().Be("unknown:0:0");

        // Re-running after ledger rollback does not clobber already-assigned ownership.
        Run(path, $"UPDATE tbl_whitelist SET ownership = 'personal', owner_person_id = '{alicePerson}', owner_label = 'Alice', needs_review = 0, ownership_lamport_ts = 5 WHERE node_id = '{peerId}'");
        Run(path, "DROP TRIGGER trg_migration_ledger_guard");
        SetLedger(path, LedgerOf045);

        await RunNewRunnerAsync(path);

        Query<string>(path, $"SELECT ownership || ':' || needs_review || ':' || ownership_lamport_ts FROM tbl_whitelist WHERE node_id = '{peerId}'")
            .Should().Be("personal:0:5");
        Query<string>(path, "SELECT person_id FROM tbl_user WHERE username = 'alice'").Should().Be(alicePerson);
    }

    [Fact]
    public async Task UserRepository_assigns_person_id_when_creating_new_user()
    {
        DapperConfig.Configure();
        var path = await CurrentSchemaDbAsync(NewDbPath());
        using var factory = new DbConnectionFactory(path);
        var repo = new UserRepository(factory);

        var id = await repo.CreateAsync(new User
        {
            Username = "carol",
            DisplayName = "Carol",
            PasswordHash = "hash",
            Role = UserRoles.User,
            CreatedAt = DateTime.UtcNow,
        });

        var loaded = await repo.GetByIdAsync(id);
        loaded.Should().NotBeNull();
        loaded!.PersonId.Should().NotBe(Guid.Empty);
    }
}

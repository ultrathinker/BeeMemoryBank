using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// The first migrations after the baseline (release 2.7): 041, the table of the invitations to a blind copy, and 042, the kind of node that redeemed (an app copy or a server blind node)
/// with the pair code a server node sends. The squash tests keep them out of their comparisons with the old chain (<c>SchemaDump</c>); this class is where they are checked, on every way a
/// database can arrive at them.
/// </summary>
public class PostBaselineTests : SquashTestBase
{
    private const string FortyOne = "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql";
    private const string FortyTwo = "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql";

    /// <summary>The trigger as 041 made it: what a database has between 041 and 042.</summary>
    private const string Trigger041 =
        @"CREATE TRIGGER trg_blind_invite_final_clears_key AFTER UPDATE OF state ON tbl_blind_invite
          WHEN NEW.state IN ('confirmed', 'refused', 'expired') AND NEW.backup_key_enc IS NOT NULL
          BEGIN UPDATE tbl_blind_invite SET backup_key_enc = NULL WHERE invite_id = NEW.invite_id; END";

    /// <summary>The table as 041 made it, from a database made by the current runner: 042's two columns and its trigger go (041's trigger comes back with <see cref="Trigger041"/>).</summary>
    private const string Back_to_041 =
        "DROP TRIGGER trg_blind_invite_final_clears_key; ALTER TABLE tbl_blind_invite DROP COLUMN kind; ALTER TABLE tbl_blind_invite DROP COLUMN pair_code_enc";

    private const string InsertRedeemed =
        @"INSERT INTO tbl_blind_invite
            (invite_id, secret_hash, label, source_name, address, trust, created_by, created_at, expires_at, state, backup_key_enc, confirm_deadline)
          VALUES ('inv1', 'hash', 'Anna', 'Alex', 'https://hub.example.org', 'public-ca', 'admin', '2026-10-21T00:00:00.0000000Z',
                  '2026-10-21T00:30:00.0000000Z', 'redeemed', x'0102030405', '2026-10-24T00:00:00.0000000Z')";

    private static long LedgerRows(string path) => Query<long>(path, "SELECT COUNT(*) FROM tbl_migration");

    private static List<(int Version, string Name)> FullLedger(string path)
    {
        using var factory = new DbConnectionFactory(path);
        using var conn = factory.CreateConnection();
        var rows = Dapper.SqlMapper.Query<(int, string)>(conn, "SELECT version, filename FROM tbl_migration ORDER BY version").ToList();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return rows;
    }

    [Fact]
    public async Task A_new_database_records_the_baseline_041_and_042()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        FullLedger(path).Should().Equal([(40, BaselineName), (41, FortyOne), (42, FortyTwo)]);
    }

    [Fact]
    public async Task The_table_its_index_and_its_trigger_are_there_and_the_table_is_empty()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        Query<long>(path, "SELECT COUNT(*) FROM tbl_blind_invite").Should().Be(0);
        Query<string>(path, "SELECT group_concat(name) FROM pragma_table_info('tbl_blind_invite')").Should().Be(
            "invite_id,secret_hash,label,source_name,address,trust,pin,created_by,created_at,expires_at,state,redeemed_at,redeemer_node_id,redeemer_key,"
            + "redeemer_name,redeemer_platform,redeemer_version,redeemer_seen_from,backup_key_enc,confirm_deadline,confirming_at,enroll_event_id,decided_at,decided_by,kind,pair_code_enc");
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_blind_invite_state'").Should().Be(1);
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_blind_invite_final_clears_key'").Should().Be(1);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("refused")]
    [InlineData("expired")]
    public async Task The_stored_backup_key_is_cleared_in_every_final_state_whatever_code_moved_the_row(string finalState)
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, InsertRedeemed);
        Query<long>(path, "SELECT length(backup_key_enc) FROM tbl_blind_invite").Should().Be(5, "while the owner has not decided, the key waits");

        Run(path, $"UPDATE tbl_blind_invite SET state = '{finalState}'");

        Query<long>(path, "SELECT backup_key_enc IS NULL FROM tbl_blind_invite").Should().Be(1, "the trigger clears it, not the caller's care");
    }

    [Fact]
    public async Task The_key_is_not_cleared_by_a_change_that_is_not_a_final_state()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, InsertRedeemed.Replace("'redeemed'", "'issued'"));

        Run(path, "UPDATE tbl_blind_invite SET state = 'redeemed'");

        Query<long>(path, "SELECT backup_key_enc IS NOT NULL FROM tbl_blind_invite").Should().Be(1);
    }

    /// <summary>The state written BEFORE a device is enrolled: the staging key is still needed to finish the enrolment, so it waits until a final state.</summary>
    [Fact]
    public async Task The_key_waits_while_the_row_is_confirming_and_is_cleared_when_it_ends()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, InsertRedeemed);

        Run(path, "UPDATE tbl_blind_invite SET state = 'confirming', confirming_at = '2026-10-01T00:00:00Z', enroll_event_id = '3f2b0c9e-0000-4000-8000-000000000001'");
        Query<long>(path, "SELECT backup_key_enc IS NOT NULL FROM tbl_blind_invite").Should().Be(1, "an interrupted confirmation needs the key to be finished");
        Query<string>(path, "SELECT enroll_event_id FROM tbl_blind_invite").Should().Be("3f2b0c9e-0000-4000-8000-000000000001", "the identity of the enrolment is durable");

        Run(path, "UPDATE tbl_blind_invite SET state = 'refused'");
        Query<long>(path, "SELECT backup_key_enc IS NULL FROM tbl_blind_invite").Should().Be(1, "undone: the key is gone with the final state");
    }

    [Fact]
    public async Task A_state_this_program_does_not_know_cannot_be_written()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        var act = () => Run(path, InsertRedeemed.Replace("'redeemed'", "'approved'"));

        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>().Which.Message.Should().Contain("CHECK");
    }

    [Fact]
    public async Task A_database_of_2_6_2_to_2_6_4_gets_041_and_042_at_its_next_start_and_a_second_start_changes_nothing()
    {
        // The ledger of the releases between the squash and this one: the baseline's row alone, and no table of 041.
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, "DROP TRIGGER trg_blind_invite_final_clears_key; DROP INDEX ix_blind_invite_state; DROP TABLE tbl_blind_invite");
        SetLedger(path, [(40, BaselineName)]);

        await RunNewRunnerAsync(path);

        FullLedger(path).Select(r => r.Version).Should().Equal(40, 41, 42);
        Query<long>(path, "SELECT COUNT(*) FROM tbl_blind_invite").Should().Be(0);
        var appliedAt = Query<string>(path, "SELECT applied_at FROM tbl_migration WHERE version = 42");

        await RunNewRunnerAsync(path);

        LedgerRows(path).Should().Be(3);
        Query<string>(path, "SELECT applied_at FROM tbl_migration WHERE version = 42").Should().Be(appliedAt, "042 is not applied again");
    }

    /// <summary>A database that ran a build with 041 only (the 2.7 line before 042): its invitations keep their rows and their staged keys, and every one of them is a device.</summary>
    [Fact]
    public async Task A_database_that_has_041_gets_042_and_keeps_its_rows_as_device_invitations()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, Back_to_041);
        Run(path, Trigger041);
        Run(path, InsertRedeemed);
        SetLedger(path, [(40, BaselineName), (41, FortyOne)]);

        await RunNewRunnerAsync(path);

        FullLedger(path).Should().Equal([(40, BaselineName), (41, FortyOne), (42, FortyTwo)]);
        Query<string>(path, "SELECT kind FROM tbl_blind_invite").Should().Be("device", "an invitation of 041 was an app copy's");
        Query<long>(path, "SELECT length(backup_key_enc) FROM tbl_blind_invite").Should().Be(5, "the key the owner has not decided on yet is still there");
        Query<long>(path, "SELECT pair_code_enc IS NULL FROM tbl_blind_invite").Should().Be(1);
        Run(path, "UPDATE tbl_blind_invite SET state = 'confirmed'");
        Query<long>(path, "SELECT backup_key_enc IS NULL FROM tbl_blind_invite").Should().Be(1, "the trigger of 042 clears the key of a device as 041's did");
    }

    [Fact]
    public async Task A_database_of_2_6_1_is_adopted_and_then_gets_041_and_042()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, "DROP TRIGGER trg_blind_invite_final_clears_key; DROP INDEX ix_blind_invite_state; DROP TABLE tbl_blind_invite");
        SetLedger(path, LegacyChain.LedgerOf("v2.6.1"));

        await RunNewRunnerAsync(path);

        FullLedger(path).Should().Equal([(40, BaselineName), (41, FortyOne), (42, FortyTwo)], "the 39 old rows became the baseline's, and 041 and 042 followed");
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'tbl_blind_invite'").Should().Be(1);
    }

    private const string InsertRedeemedServer =
        @"INSERT INTO tbl_blind_invite
            (invite_id, secret_hash, label, source_name, address, trust, created_by, created_at, expires_at, state, kind, pair_code_enc, confirm_deadline)
          VALUES ('inv2', 'hash', 'Anna server', 'Alex', 'https://hub.example.org', 'public-ca', 'admin', '2026-10-21T00:00:00.0000000Z',
                  '2026-10-21T00:30:00.0000000Z', 'redeemed', 'server', x'0A0B0C0D', '2026-10-24T00:00:00.0000000Z')";

    [Fact]
    public async Task An_invitation_is_a_device_one_unless_it_says_it_is_a_servers_and_there_are_no_other_kinds()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, InsertRedeemed);
        Query<string>(path, "SELECT kind FROM tbl_blind_invite").Should().Be("device");
        Run(path, "DELETE FROM tbl_blind_invite");
        Run(path, InsertRedeemedServer);
        Query<string>(path, "SELECT kind FROM tbl_blind_invite").Should().Be("server");

        var act = () => Run(path, InsertRedeemedServer.Replace("'inv2'", "'inv3'").Replace("'server'", "'robot'"));

        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>().Which.Message.Should().Contain("CHECK");
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("refused")]
    [InlineData("expired")]
    public async Task The_stored_pair_code_is_cleared_in_every_final_state_whatever_code_moved_the_row(string finalState)
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, InsertRedeemedServer);
        Query<long>(path, "SELECT length(pair_code_enc) FROM tbl_blind_invite").Should().Be(4, "while the owner has not decided, the code waits");

        Run(path, $"UPDATE tbl_blind_invite SET state = '{finalState}'");

        Query<long>(path, "SELECT pair_code_enc IS NULL FROM tbl_blind_invite").Should().Be(1, "the trigger clears it, not the caller's care");
        Query<string>(path, "SELECT kind FROM tbl_blind_invite").Should().Be("server", "what kind of node it was is not a secret and stays");
    }

    [Fact]
    public async Task The_pair_code_waits_while_the_row_is_confirming_and_is_not_cleared_by_a_change_that_is_not_a_final_state()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, InsertRedeemedServer.Replace("'redeemed'", "'issued'"));

        Run(path, "UPDATE tbl_blind_invite SET state = 'redeemed'");
        Query<long>(path, "SELECT pair_code_enc IS NOT NULL FROM tbl_blind_invite").Should().Be(1);
        Run(path, "UPDATE tbl_blind_invite SET state = 'confirming', confirming_at = '2026-10-01T00:00:00Z'");

        Query<long>(path, "SELECT pair_code_enc IS NOT NULL FROM tbl_blind_invite").Should().Be(1, "an interrupted confirmation needs the code to be finished");
    }

    [Fact]
    public void The_table_never_travels_in_a_snapshot_it_is_not_on_the_replicated_list()
    {
        SnapshotTables.Replicated.Should().NotContain("tbl_blind_invite");
        SnapshotTables.Control.Should().NotContain("tbl_blind_invite");
        SnapshotTables.StrippedByDropping.Should().NotContain("tbl_blind_invite", "it is emptied, not dropped: the receiving node needs the table to exist");
    }
}

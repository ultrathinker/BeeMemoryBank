using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey.Tests;

public sealed class PeerRevokeStepTests
{
    private sealed class NullProgress : IRekeyProgress
    {
        public void Report(string step, long done, long total, string? note = null) { }
    }

    [Fact]
    public async Task OfflineRekey_And_PeerRevokeStep_Keep_TblAccountsHome_And_The_Last_Issued_List_Version_While_Clearing_What_Peers_Sent()
    {
        // a re-keyed accounts node continues its list versions; a restart at 1 would be dropped as old by every shared node.
        RekeyTables.Main["tbl_accounts_home"].Should().Be(TableFate.Plaintext);
        RekeyTables.Main["tbl_shared_doors_state"].Should().Be(TableFate.Plaintext);

        using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE tbl_whitelist (
                    node_id TEXT PRIMARY KEY, display_name TEXT NOT NULL, status TEXT NOT NULL,
                    deleted_at TEXT, updated_at TEXT, lamport_ts INTEGER NOT NULL, source_node_id TEXT,
                    member_lamport_ts INTEGER NOT NULL DEFAULT 0, member_source_node_id TEXT
                );
                CREATE TABLE tbl_blind_invite (id INTEGER PRIMARY KEY);
                CREATE TABLE tbl_peer_endpoint (id INTEGER PRIMARY KEY);
                CREATE TABLE tbl_peer_channel (id INTEGER PRIMARY KEY);
                CREATE TABLE tbl_node_state (node_id TEXT PRIMARY KEY);
                CREATE TABLE tbl_accounts_home (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    node_id TEXT NOT NULL, lamport_ts INTEGER NOT NULL DEFAULT 0,
                    source_node_id TEXT, event_id TEXT, updated_at TEXT NOT NULL
                );
                CREATE TABLE tbl_shared_doors_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    self_ownership TEXT NOT NULL DEFAULT 'unknown',
                    self_ownership_lamport_ts INTEGER NOT NULL DEFAULT 0,
                    self_ownership_source_node_id TEXT,
                    last_issued_version INTEGER NOT NULL DEFAULT 0,
                    last_published_digest TEXT,
                    latest_received_version INTEGER NOT NULL DEFAULT 0,
                    latest_event_id TEXT,
                    latest_author_node_id TEXT,
                    latest_payload_json TEXT,
                    pending_apply INTEGER NOT NULL DEFAULT 0,
                    applied_version INTEGER NOT NULL DEFAULT 0,
                    applied_at TEXT,
                    primary_person_id TEXT,
                    last_error TEXT,
                    accounts_home_changed INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO tbl_accounts_home (id, node_id, lamport_ts, updated_at)
                VALUES (1, '11111111-1111-4111-8111-111111111111', 5, '2026-11-16T00:00:00Z');
                INSERT INTO tbl_shared_doors_state (id, self_ownership, last_issued_version, last_published_digest, latest_received_version,
                    latest_event_id, latest_author_node_id, latest_payload_json, pending_apply, applied_version, last_error)
                VALUES (1, 'shared', 7, 'digest', 4, 'e', '22222222-2222-4222-8222-222222222222', '{}', 1, 3, 'x');";
            await cmd.ExecuteNonQueryAsync();
        }

        var ctx = new RekeyContext(
            ".", ".", conn, null,
            new RekeyKeys(new byte[32], [new byte[32]], [], new byte[32]),
            Guid.NewGuid(), new NullProgress(), CancellationToken.None);

        var step = new PeerRevokeStep();
        await step.RunAsync(ctx);
        (await step.VerifyAsync(ctx)).Should().BeEmpty();

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM tbl_accounts_home";
            Convert.ToInt64(await check.ExecuteScalarAsync()).Should().Be(1);
            check.CommandText = "SELECT last_issued_version FROM tbl_shared_doors_state";
            Convert.ToInt64(await check.ExecuteScalarAsync()).Should().Be(7, "the next list this node issues is version 8, above what its shared nodes hold");
            check.CommandText = "SELECT self_ownership FROM tbl_shared_doors_state";
            (await check.ExecuteScalarAsync()).Should().Be("shared");
            check.CommandText = @"SELECT latest_received_version + pending_apply + applied_version + COALESCE(length(latest_payload_json), 0)
                + COALESCE(length(last_published_digest), 0) + COALESCE(length(last_error), 0) FROM tbl_shared_doors_state";
            Convert.ToInt64(await check.ExecuteScalarAsync()).Should().Be(0, "what the revoked peers sent, and the digest of the peers it published to, goes");
        }
    }
}

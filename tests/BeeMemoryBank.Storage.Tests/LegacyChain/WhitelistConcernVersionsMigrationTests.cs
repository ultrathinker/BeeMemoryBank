using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// Migration 045: a version per concern on a whitelist row (membership, superadmin, name, address, TLS trust). On a database of 044 every concern
/// starts at the row's own version, so nothing an update or a revoke is compared against changes at the upgrade (ties included), and the fields
/// are undated (their writer is not known); run again over a database that has the columns, it leaves the versions the applier or the repair set.
/// </summary>
public class WhitelistConcernVersionsMigrationTests : SquashTestBase
{
    private const string FortyFour = "BeeMemoryBank.Storage.Migrations.044_peer_channels.sql";

    private static readonly string[] Columns =
    [
        "member_lamport_ts", "member_source_node_id", "superadmin_lamport_ts", "superadmin_source_node_id", "name_lamport_ts", "name_source_node_id",
        "address_lamport_ts", "address_source_node_id", "tls_lamport_ts", "tls_source_node_id",
    ];

    private static readonly string[] ColumnsWithTheMask = [.. Columns, "undated_fields"];

    private static readonly (int, string)[] LedgerOf044 =
    [
        (40, BaselineName), (41, "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql"),
        (42, "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql"), (43, "BeeMemoryBank.Storage.Migrations.043_password_hint.sql"),
        (44, FortyFour),
    ];

    private const string Author = "aaaaaaaa-0000-4000-8000-000000000001";

    [Fact]
    public async Task A_new_database_has_the_eleven_columns_with_unversioned_defaults()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        Query<string>(path, "SELECT group_concat(name) FROM (SELECT name FROM pragma_table_info('tbl_whitelist') WHERE name LIKE '%\\_lamport\\_ts' ESCAPE '\\' OR name LIKE '%\\_source\\_node\\_id' ESCAPE '\\' ORDER BY cid)")
            .Should().Be(string.Join(",", Columns));
        foreach (var column in Columns.Where(c => c.EndsWith("_lamport_ts", StringComparison.Ordinal)))
        {
            Query<long>(path, $"SELECT \"notnull\" FROM pragma_table_info('tbl_whitelist') WHERE name = '{column}'").Should().Be(1);
            Query<string>(path, $"SELECT dflt_value FROM pragma_table_info('tbl_whitelist') WHERE name = '{column}'").Should().Be("0");
        }
        Query<string>(path, "SELECT type || ',' || \"notnull\" || ',' || dflt_value FROM pragma_table_info('tbl_whitelist') WHERE name = 'undated_fields'")
            .Should().Be("INTEGER,1,0");
        MigrationRunner.AdditiveMigrations.Should().Contain(45, "the migration only adds columns with defaults");
    }

    [Fact]
    public async Task A_database_of_044_gets_every_concern_at_the_rows_version_and_keeps_every_value()
    {
        var path = await DatabaseOf044Async();
        Run(path, $@"
            INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at, lamport_ts, source_node_id, api_address)
            VALUES ('bbbbbbbb-0000-4000-8000-000000000001', 'Active', x'01', 'A', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 7311, '{Author}', 'https://a.example');
            INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, created_at, updated_at, deleted_at, lamport_ts, source_node_id)
            VALUES ('bbbbbbbb-0000-4000-8000-000000000002', 'Revoked', x'02', 'R', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z', 7400, '{Author}');
            INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, created_at, updated_at)
            VALUES ('bbbbbbbb-0000-4000-8000-000000000003', 'Joined from 2.7.5', x'03', 'A', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");

        await RunNewRunnerAsync(path);

        FullLedger(path).Select(r => r.Version).Should().Equal(40, 41, 42, 43, 44, 45);
        foreach (var (id, ts, source) in new[] { ("1", 7311L, Author), ("2", 7400L, Author), ("3", 0L, (string?)null) })
        {
            var node = $"bbbbbbbb-0000-4000-8000-00000000000{id}";
            foreach (var concern in new[] { "member", "superadmin", "name", "address", "tls" })
            {
                Query<long>(path, $"SELECT {concern}_lamport_ts FROM tbl_whitelist WHERE node_id = '{node}'").Should().Be(ts, $"{node} {concern}");
                Query<string?>(path, $"SELECT {concern}_source_node_id FROM tbl_whitelist WHERE node_id = '{node}'").Should().Be(source, $"{node} {concern}");
            }
            // A field's writer is not known: every field is undated (superadmin 2 + name 4 + address 8 + TLS 16), membership never.
            Query<long>(path, $"SELECT undated_fields FROM tbl_whitelist WHERE node_id = '{node}'").Should().Be(30, node);
        }
        Query<string>(path, "SELECT group_concat(status || display_name || is_superadmin, ',') FROM (SELECT * FROM tbl_whitelist ORDER BY node_id)")
            .Should().Be("AActive1,RRevoked0,AJoined from 2.7.50", "the migration revokes, promotes or renames nothing");
    }

    [Fact]
    public async Task Run_again_it_leaves_the_versions_of_this_version_as_they_are()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        // A row as the applier left it: membership at the add, the name at a later rename, the row at the newest.
        Run(path, $@"
            INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, created_at, updated_at, lamport_ts, source_node_id,
                member_lamport_ts, member_source_node_id, superadmin_lamport_ts, superadmin_source_node_id, name_lamport_ts, name_source_node_id,
                address_lamport_ts, address_source_node_id, tls_lamport_ts, tls_source_node_id)
            VALUES ('bbbbbbbb-0000-4000-8000-000000000009', 'Renamed', x'09', 'A', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 90, '{Author}',
                10, '{Author}', 10, '{Author}', 90, '{Author}', 10, '{Author}', 10, '{Author}');");
        SetLedger(path, LedgerOf044);

        await RunNewRunnerAsync(path);

        FullLedger(path).Select(r => r.Version).Should().Equal(40, 41, 42, 43, 44, 45);
        Query<string>(path, "SELECT member_lamport_ts || ',' || name_lamport_ts || ',' || tls_lamport_ts FROM tbl_whitelist WHERE node_id = 'bbbbbbbb-0000-4000-8000-000000000009'")
            .Should().Be("10,90,10", "a row with a version on a concern is not one from before 045");
    }

    private static List<(int Version, string Name)> FullLedger(string path)
    {
        using var factory = new DbConnectionFactory(path);
        using var conn = factory.CreateConnection();
        var rows = Dapper.SqlMapper.Query<(int, string)>(conn, "SELECT version, filename FROM tbl_migration ORDER BY version").ToList();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return rows;
    }

    /// <summary>The current schema with the ten columns taken away and the ledger of a node that ran 044: what a node of this version starts from.</summary>
    private async Task<string> DatabaseOf044Async()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        Run(path, string.Join("; ", ColumnsWithTheMask.Select(c => $"ALTER TABLE tbl_whitelist DROP COLUMN {c}")));
        SetLedger(path, LedgerOf044);
        return path;
    }
}

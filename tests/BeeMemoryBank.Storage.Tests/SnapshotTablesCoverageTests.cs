using BeeMemoryBank.Storage.Sqlite;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Forces a decision about every table in the schema: does it travel to other nodes, or not?
///
/// <para>Filtering used to work by deny-list — name the secrets, ship the rest — so the answer for
/// any table nobody thought about was "ship it, with its contents". Several went out that way
/// (<c>tbl_remote_api_token</c>, <c>tbl_search_index_key</c>, <c>tbl_dek_rotation_state</c>), and
/// nothing anywhere would have told us. The filter is now an allow-list, which makes the silent
/// default safe, but says nothing about whether a new CONTENT table was remembered — a table left
/// off <see cref="SnapshotTables.Replicated"/> simply never reaches a joining node, and the symptom
/// is missing content on one machine months later.</para>
///
/// <para>So this test enumerates the real schema and requires every table to appear in exactly one
/// of the lists below. Adding a migration with a new table turns this red until someone writes down
/// which kind it is. That is the entire point: the failure is at the keyboard, not in production.</para>
/// </summary>
public class SnapshotTablesCoverageTests
{
    /// <summary>
    /// Tables that stay on the node that owns them. Each entry is a deliberate "no" — node
    /// identity, credentials, per-user state, local bookkeeping, or an index the receiving node
    /// rebuilds for itself.
    /// </summary>
    private static readonly string[] LocalOnly =
    [
        // Identity, keys, accounts — the reason peer packages exist at all.
        "tbl_node_identity", "tbl_key_slot", "tbl_user", "tbl_session",
        "tbl_agent", "tbl_agent_access", "tbl_folder_acl_entry",
        "tbl_role", "tbl_role_folder_acl_entry",

        // Credentials this node holds for OTHER people's nodes, and tokens it issued for its own.
        "tbl_remote_account", "tbl_remote_subscription", "tbl_remote_api_token",

        // Per-user, and meaningless against another node's user ids.
        "tbl_favorite",

        // The event log and everything describing this node's place in the network. A joiner
        // starts its own history; a restored node must not inherit the originator's positions.
        "tbl_event", "tbl_sync_position", "tbl_sync_push_position", "tbl_sync_quarantine",
        "tbl_whitelist", "tbl_restore_replay_shield", "tbl_restore_event_state",
        "tbl_compaction_log", "tbl_dek_rotation_state",

        // Audit trails: this node's record of what happened here.
        "tbl_audit_log", "tbl_hard_delete_audit",

        // Search state. Rebuilt locally from the content that IS replicated, and the index key is
        // wrapped with this node's own DEK.
        "tbl_search_index_key", "tbl_search_index_manifest", "tbl_search_segment_tombstone",
        "tbl_article_chunk_embedding"
    ];

    [Fact]
    public void EveryTableInTheSchema_IsClassified()
    {
        var schemaTables = ReadSchemaTableNames();
        schemaTables.Should().NotBeEmpty("the migrations must have produced a schema to check");

        var classified = new HashSet<string>(
            SnapshotTables.Replicated
                .Concat(SnapshotTables.SchemaMeta)
                .Concat(SnapshotTables.StrippedByDropping)
                .Concat(LocalOnly),
            StringComparer.OrdinalIgnoreCase);

        var unclassified = schemaTables.Where(t => !classified.Contains(t)).ToList();

        unclassified.Should().BeEmpty(
            "every table has to be either replicated to peers or deliberately kept local. " +
            "Add each of these to SnapshotTables.Replicated (content other nodes need) or to " +
            "LocalOnly in this test (node-local), and say why in a comment");
    }

    [Fact]
    public void NoTableIsBothReplicatedAndLocal()
    {
        var replicated = new HashSet<string>(SnapshotTables.Replicated, StringComparer.OrdinalIgnoreCase);

        // A table in both lists means two people answered the question differently, which is how
        // the three drifted copies of this list came about in the first place.
        replicated.Overlaps(LocalOnly).Should().BeFalse();
        replicated.Overlaps(SnapshotTables.StrippedByDropping).Should().BeFalse();
        replicated.Overlaps(SnapshotTables.SchemaMeta).Should().BeFalse();
    }

    [Fact]
    public void BlobsAreImportedFirst()
    {
        // Article bodies and versions address their ciphertext by hash into tbl_blob. Import it
        // later and a crash mid-import leaves bodies whose bytes are not there yet; the row looks
        // fine and reads as empty. The list is used as an ordered import plan, so this is a
        // property of the list itself, not of any one caller.
        SnapshotTables.Replicated[0].Should().Be("tbl_blob");
    }

    [Fact]
    public void ContentTablesPeersDependOn_AreReplicated()
    {
        // Pins the drift that started this: two of the three copies of the list omitted comments
        // and article history, so joiners silently received neither.
        SnapshotTables.Replicated.Should().Contain("tbl_comment");
        SnapshotTables.Replicated.Should().Contain("tbl_article_version");
        SnapshotTables.Replicated.Should().Contain("tbl_article_body");
    }

    private static List<string> ReadSchemaTableNames()
    {
        using var factory = DbConnectionFactory.CreateInMemory($"snapshot_tables_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(factory);
        runner.RunMigrationsAsync().GetAwaiter().GetResult();

        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // FTS shadow tables are an implementation detail of a virtual table the receiving node
        // creates for itself; they are never imported or filtered by name.
        cmd.CommandText = @"SELECT name FROM sqlite_master
                            WHERE type = 'table'
                              AND name NOT LIKE 'sqlite_%'
                              AND name NOT LIKE 'fts_%'
                              AND name NOT LIKE '%_fts%'";
        using var reader = cmd.ExecuteReader();

        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}

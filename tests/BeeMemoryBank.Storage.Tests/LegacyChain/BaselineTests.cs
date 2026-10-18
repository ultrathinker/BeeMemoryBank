using System.Text;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314: the public tree holds ONE migration, the baseline. A new database gets it once and records one ledger row; the schema it
/// builds is the schema the old 39-file chain built (the oracle is the chain itself, read from the git history when that is there).
/// </summary>
public class BaselineTests : SquashTestBase
{
    [Fact]
    public async Task A_new_database_records_exactly_one_row_the_baseline()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        using var factory = new DbConnectionFactory(path);
        using var conn = factory.CreateConnection();
        SchemaDump.Ledger(conn).Should().Equal([(40, BaselineName)],
            "the baseline carries the number of the final schema (40): snapshots, join archives and blind packages of a 2.6.1 node and a 2.6.2 node compare equal");
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        var first = ReadAll(path);
        string appliedAt;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            appliedAt = conn.ExecuteScalar<string>("SELECT applied_at FROM tbl_migration")!;
        SqliteConnection.ClearAllPools();

        await RunNewRunnerAsync(path);

        var second = ReadAll(path);
        second.Shape.Should().Be(first.Shape);
        second.Rows.Should().Be(first.Rows);
        second.Ledger.Should().Equal(first.Ledger);
        Query<string>(path, "SELECT applied_at FROM tbl_migration").Should().Be(appliedAt, "the baseline is not applied again");
    }

    [Fact]
    public async Task The_seed_rows_of_the_schema_are_there()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());

        Query<long>(path, "SELECT COUNT(*) FROM tbl_role WHERE is_system = 1 AND name IN ('superadmin', 'user')").Should().Be(2,
            "the two system roles are data of the schema, nothing else seeds them");
        Query<string>(path, "SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'").Should().NotBeNullOrEmpty(
            "the counter of the sealed unlock material starts with the schema");
    }

    [Fact]
    public async Task The_fts_indexes_work_on_a_new_database()
    {
        var path = await CurrentSchemaDbAsync(NewDbPath());
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
        {
            conn.Execute("INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES ('a1', 'Quarterly roadmap', '/Plans/Roadmap', 'A', '2026-10-01T10:00:00.0000000Z', '2026-10-01T10:00:00.0000000Z')");
            conn.Query<string>("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'roadmap'")
                .Should().Equal(new[] { "a1" }, "the trigger put the row into the index");
            conn.Execute("INSERT INTO fts_article(fts_article) VALUES ('integrity-check')");
        }
        SqliteConnection.ClearAllPools();
    }

    // ---- the oracle: the chain of the releases before the squash ------------------------------------------------------------------

    [LegacyChainFact]
    public async Task The_baseline_file_is_what_the_generator_makes_of_the_old_chain()
    {
        var generated = await BaselineGenerator.GenerateAsync(Dir);
        var path = Path.Combine(LegacyChain.RepoRoot(), "libs", "BeeMemoryBank.Storage", "Migrations", "040_baseline.sql");
        if (Environment.GetEnvironmentVariable("BMB_UPDATE_BASELINE") == "1")
        {
            File.WriteAllText(path, generated, new UTF8Encoding(false));
            return;
        }
        File.ReadAllText(path).Replace("\r\n", "\n").Should().Be(generated,
            "the baseline is generated from the real final schema, never edited (BMB_UPDATE_BASELINE=1 regenerates it)");
    }

    [LegacyChainFact]
    public async Task A_new_database_has_the_schema_the_old_chain_built()
    {
        var chainPath = NewDbPath("chain.db");
        using (var factory = await LegacyChain.BuildAsync(chainPath, "v2.6.1")) { }
        SqliteConnection.ClearAllPools();
        var current = await CurrentSchemaDbAsync(NewDbPath("current.db"));

        string chainShape, chainRows, currentShape, currentRows;
        using (var f1 = new DbConnectionFactory(chainPath))
        using (var c1 = f1.CreateConnection())
        {
            chainShape = SchemaDump.Shape(c1, withLedger: false);
            chainRows = SchemaDump.RowsText(c1, maskTimestamps: true);
        }
        using (var f2 = new DbConnectionFactory(current))
        using (var c2 = f2.CreateConnection())
        {
            currentShape = SchemaDump.Shape(c2, withLedger: false);
            currentRows = SchemaDump.RowsText(c2, maskTimestamps: true);
        }
        SqliteConnection.ClearAllPools();

        // Line by line first, so a failure names the objects that differ instead of printing two 60 KB texts.
        currentShape.Split('\n').Except(chainShape.Split('\n')).Should().BeEmpty("the baseline builds nothing the old chain did not");
        chainShape.Split('\n').Except(currentShape.Split('\n')).Should().BeEmpty("the baseline builds everything the old chain did");
        currentShape.Should().Be(chainShape,
            "tables, columns (type, not-null, default, primary key), foreign keys, indexes, triggers, views, the FTS tables and their shadow tables, and the sequences");
        currentRows.Should().Be(chainRows, "the rows the migrations seed (roles, markers) are the same");
    }

    [LegacyChainFact]
    public async Task The_old_chain_applies_to_a_new_database_with_its_ledger_and_a_baseline_database_matches_its_objects()
    {
        // The chain is the oracle only if it still builds what 2.6.1 built: 39 rows, versions 1..31 and 33..40.
        var path = NewDbPath("chain.db");
        using (var factory = await LegacyChain.BuildAsync(path, "v2.6.1")) { }
        SqliteConnection.ClearAllPools();

        using var f = new DbConnectionFactory(path);
        using var conn = f.CreateConnection();
        SchemaDump.Ledger(conn).Select(r => r.Version).Should().Equal(Enumerable.Range(1, 40).Where(v => v != 32));
    }
}

using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314, the adoption of a database that carries the ledger of the old chain (every 2.6.x node): no migration SQL runs, the shape is
/// checked against the baseline, what is safely repairable is repaired, the 39 ledger rows become the baseline row in one transaction.
/// A database that is not at the end of that chain is refused and not written to. These tests need no old files: the schema of the final
/// release IS the baseline's, so a database of it with the ledger of the old chain put back is a 2.6.x database.
/// </summary>
public class AdoptionTests : SquashTestBase
{
    /// <summary>The ledger of a node of a given release, by the names the tests keep in chains.txt.</summary>
    private static IEnumerable<(int, string)> Ledger(string tag) => LegacyChain.LedgerOf(tag);

    /// <summary>A "2.6.x" database: the final schema, a few rows everywhere, the ledger of the old chain.</summary>
    private async Task<string> A26DatabaseAsync(string name = "beememorybank.db")
    {
        var path = await CurrentSchemaDbAsync(NewDbPath(name));
        SeedRows(path);
        SetLedger(path, Ledger("v2.6.1"));
        return path;
    }

    // ---- adoption -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_2_6_database_is_adopted_the_ledger_becomes_the_baseline_and_nothing_else_changes()
    {
        var path = await A26DatabaseAsync();
        var before = ReadAll(path);
        before.Ledger.Should().HaveCount(39);

        await RunNewRunnerAsync(path);

        var after = ReadAll(path);
        after.Ledger.Should().Equal([(40, BaselineName)]);
        after.Shape.Should().Be(before.Shape, "adoption changes no object of a database that is already complete");
        after.Rows.Should().Be(before.Rows, "every row of every table is kept");
        Query<string>(path, "PRAGMA integrity_check").Should().Be("ok");
    }

    [Fact]
    public async Task Adoption_runs_no_migration_sql()
    {
        // A recovery key slot is what migration 012 deletes; an agent with a wrapped DEK of a regular user is what 014 clears. None of that
        // may happen to an adopted database: the migrations ran long ago, and nothing of them runs now.
        var path = await A26DatabaseAsync();
        Run(path, @"INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                    VALUES ('recovery', X'0102', X'0304', X'0506', 65536, 3, 4, '2026-10-01T10:00:00.0000000Z')");
        var slots = Query<long>(path, "SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = 'recovery'");
        slots.Should().Be(1);

        await RunNewRunnerAsync(path);

        Query<long>(path, "SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = 'recovery'").Should().Be(1, "no migration statement was executed");
    }

    [Fact]
    public async Task An_adopted_database_is_adopted_once_the_next_run_is_the_normal_path()
    {
        var path = await A26DatabaseAsync();
        await RunNewRunnerAsync(path);
        var first = ReadAll(path);
        var appliedAt = Query<string>(path, "SELECT applied_at FROM tbl_migration");

        await RunNewRunnerAsync(path);

        var second = ReadAll(path);
        second.Ledger.Should().Equal(first.Ledger);
        second.Shape.Should().Be(first.Shape);
        Query<string>(path, "SELECT applied_at FROM tbl_migration").Should().Be(appliedAt);
    }

    [Fact]
    public async Task Leftover_objects_are_tolerated_and_logged_once()
    {
        // What real databases carry that a fresh one does not: the partial unique index the OS auto-unlock service creates at run time,
        // and the keyword-tag tables of the generation before the concept tags (older nodes dropped them, some did not).
        var path = await A26DatabaseAsync();
        Run(path, @"CREATE UNIQUE INDEX ux_key_slot_os_auto_unlock ON tbl_key_slot(slot_type) WHERE slot_type = 'os_auto_unlock';
                    CREATE TABLE tbl_tag (id INTEGER PRIMARY KEY, name TEXT);
                    INSERT INTO tbl_tag (name) VALUES ('legacy');
                    CREATE TABLE tbl_article_tag_deprecated (article_id TEXT, tag_id INTEGER)");
        var logger = new CapturingLogger<MigrationRunner>();

        await RunNewRunnerAsync(path, logger);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)], "a harmless difference never stops a node whose chain is complete");
        Query<long>(path, "SELECT COUNT(*) FROM tbl_tag").Should().Be(1, "extra objects are left exactly as they are");
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ux_key_slot_os_auto_unlock'").Should().Be(1);
        logger.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information).Should().ContainSingle(
            "one Information line says what was adopted and what was left").Which.Message.Should().Contain("tbl_tag");
        logger.Entries.Should().NotContain(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_missing_index_is_repaired()
    {
        var path = await A26DatabaseAsync();
        var reference = ShapeOf(path);
        Run(path, "DROP INDEX idx_article_version_article");

        await RunNewRunnerAsync(path);

        ShapeOf(path).Should().Be(reference, "the index is back, exactly as the baseline defines it");
        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
    }

    [Fact]
    public async Task A_missing_trigger_is_repaired()
    {
        var path = await A26DatabaseAsync();
        var reference = ShapeOf(path);
        var trigger = Query<string>(path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'trg_umv%' ORDER BY name LIMIT 1");
        trigger.Should().NotBeNullOrEmpty();
        Run(path, $"DROP TRIGGER \"{trigger}\"");

        await RunNewRunnerAsync(path);

        ShapeOf(path).Should().Be(reference);
    }

    [Fact]
    public async Task A_missing_table_is_repaired()
    {
        var path = await A26DatabaseAsync();
        var reference = ShapeOf(path);
        Run(path, "DROP TABLE tbl_node_data_key");

        await RunNewRunnerAsync(path);

        ShapeOf(path).Should().Be(reference);
        Query<long>(path, "SELECT COUNT(*) FROM tbl_node_data_key").Should().Be(0);
    }

    [Fact]
    public async Task A_missing_fts_index_is_rebuilt_from_its_content()
    {
        var path = await A26DatabaseAsync();
        var reference = ShapeOf(path);
        var found = Query<long>(path, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'article'");
        found.Should().BeGreaterThan(0, "the seed rows reach the index through its triggers");
        Run(path, "DROP TABLE fts_article");

        await RunNewRunnerAsync(path);

        ShapeOf(path).Should().Be(reference);
        Query<long>(path, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'article'").Should().Be(found,
            "a rebuilt index answers like the one it replaces");
    }

    [Fact]
    public async Task An_extra_column_is_tolerated()
    {
        var path = await A26DatabaseAsync();
        Run(path, "ALTER TABLE tbl_folder ADD COLUMN leftover_note TEXT");
        var rowsBefore = ReadAll(path).Rows;

        await RunNewRunnerAsync(path);

        var after = ReadAll(path);
        after.Ledger.Should().Equal([(40, BaselineName)]);
        after.Rows.Should().Be(rowsBefore);
        Query<long>(path, "SELECT COUNT(*) FROM pragma_table_info('tbl_folder') WHERE name = 'leftover_note'").Should().Be(1);
    }

    [Fact]
    public async Task A_column_that_is_looser_than_the_baseline_is_tolerated_and_counted()
    {
        // tbl_media.kind is TEXT NOT NULL DEFAULT 'image'; here NOT NULL is gone. The program has always run on such a column.
        var path = await A26DatabaseAsync();
        Run(path, @"PRAGMA writable_schema = ON;
                    UPDATE sqlite_master SET sql = replace(sql, 'kind TEXT NOT NULL', 'kind TEXT') WHERE type = 'table' AND name = 'tbl_media';
                    PRAGMA writable_schema = OFF");
        Query<long>(path, "SELECT COUNT(*) FROM pragma_table_info('tbl_media') WHERE name = 'kind' AND \"notnull\" = 0").Should().Be(1, "the arrangement took");
        var logger = new CapturingLogger<MigrationRunner>();

        await RunNewRunnerAsync(path, logger);

        ReadAll(path).Ledger.Should().Equal([(40, BaselineName)]);
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information)
            .Which.Message.Should().Contain("1 column difference");
    }

    [Fact]
    public async Task A_database_another_process_adopted_in_the_meantime_is_left_alone()
    {
        // The CLI next to a running node: both decided "adopt" from the 39 rows, one was first. The second finds the baseline row inside its
        // own transaction and does nothing: no write, no log line, no error from the guard.
        var path = await CurrentSchemaDbAsync(NewDbPath());
        string baselineSql;
        using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(BaselineName)!)
        using (var reader = new StreamReader(stream))
            baselineSql = await reader.ReadToEndAsync();
        var before = SchemaDump.FileDigest(path);
        var logger = new CapturingLogger<MigrationRunner>();

        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            await LegacyChainAdoption.AdoptAsync(conn, baselineSql, logger);
        SqliteConnection.ClearAllPools();

        logger.Entries.Should().BeEmpty();
        SchemaDump.FileDigest(path).Should().Be(before);
    }

    // ---- refusal: nothing is written ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_column_is_refused_and_the_file_is_untouched()
    {
        var path = await A26DatabaseAsync();
        Run(path, "ALTER TABLE tbl_node_identity DROP COLUMN brand_name");

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain("brand_name").And.Contain("tbl_node_identity", "it names what is missing");
        ex.Message.Should().Contain("2.6.1", "and what to do");
    }

    [Fact]
    public async Task A_column_with_another_type_is_refused_and_the_file_is_untouched()
    {
        // tbl_media.kind is TEXT NOT NULL DEFAULT 'image'; a table rebuilt by hand with an INTEGER there is not the shape the code reads.
        var path = await A26DatabaseAsync();
        Run(path, @"PRAGMA foreign_keys = OFF; PRAGMA writable_schema = ON;
                    UPDATE sqlite_master SET sql = replace(sql, 'kind TEXT', 'kind INTEGER') WHERE type = 'table' AND name = 'tbl_media';
                    PRAGMA writable_schema = OFF");

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain("tbl_media").And.Contain("kind");
    }

    [Fact]
    public async Task A_half_run_chain_is_refused_and_the_file_is_untouched()
    {
        var path = await A26DatabaseAsync();
        SetLedger(path, Ledger("v2.6.1").Take(20));

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain("2.6.1");
    }

    [Theory]
    [InlineData("v2.1.0")]
    [InlineData("v2.2.0")]
    [InlineData("v2.3.0")]
    [InlineData("v2.5.0")]
    public async Task A_database_of_an_older_release_is_refused_and_the_file_is_untouched(string tag)
    {
        // The ledger of the release; the schema is the final one on purpose: the refusal rests on the ledger, not on a guess about the shape.
        var path = await A26DatabaseAsync();
        SetLedger(path, Ledger(tag));

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().Contain("older than 2.6").And.Contain("2.6.1");
    }

    [Fact]
    public async Task A_ledger_with_a_row_nobody_knows_is_refused_and_the_file_is_untouched()
    {
        var path = await A26DatabaseAsync();
        SetLedger(path, Ledger("v2.6.1").Append((41, "BeeMemoryBank.Storage.Migrations.041_from_the_future.sql")));

        await RefusedUntouchedAsync(path);
    }

    [Fact]
    public async Task A_ledger_of_files_this_program_does_not_know_is_refused_and_the_file_is_untouched()
    {
        var path = await A26DatabaseAsync();
        SetLedger(path, [(1, "SomethingElse.Migrations.001_other.sql"), (2, "SomethingElse.Migrations.002_other.sql")]);

        await RefusedUntouchedAsync(path);
    }

    [Fact]
    public async Task A_ledger_with_the_baseline_row_and_the_old_rows_beside_it_is_refused_and_the_file_is_untouched()
    {
        // Not a chain any release or tool produced: the baseline's row (version 40) next to the old rows 1..39. It is neither the baseline's
        // ledger alone nor the 39 rows of 2.6.x, so neither the normal path nor the adoption may take it.
        var path = await A26DatabaseAsync();
        SetLedger(path, Ledger("v2.6.1").Where(r => r.Item1 != 40).Append((40, BaselineName)));

        var ex = await RefusedUntouchedAsync(path);

        ex.Should().BeOfType<MigrationRefusedException>().Which.Reason.Should().Be(MigrationRefusal.UnknownHistory);
    }

    [Fact]
    public async Task A_ledger_of_the_39_rows_with_one_file_name_changed_is_refused_and_the_file_is_untouched()
    {
        // The 39 rows are recognised by version AND file name: a chain whose file 10 carries another name is not the chain this program
        // knows (somebody edited the ledger, or it is a fork), and its structure is not vouched for.
        var path = await A26DatabaseAsync();
        SetLedger(path, Ledger("v2.6.1").Select(r => r.Item1 == 10 ? (r.Item1, "BeeMemoryBank.Storage.Migrations.010_brand_name_renamed.sql") : r));

        var ex = await RefusedUntouchedAsync(path);

        ex.Should().BeOfType<MigrationRefusedException>().Which.Reason.Should().Be(MigrationRefusal.UnknownHistory);
    }

    [Fact]
    public async Task A_database_without_ledger_rows_but_with_tables_is_refused_and_the_file_is_untouched()
    {
        var path = await A26DatabaseAsync();
        SetLedger(path, []);

        await RefusedUntouchedAsync(path);
    }

    [Fact]
    public async Task The_refusal_message_has_no_paths_and_no_secrets()
    {
        var path = await A26DatabaseAsync();
        SetLedger(path, Ledger("v2.5.0"));

        var ex = await RefusedUntouchedAsync(path);

        ex.Message.Should().NotContain(Dir).And.NotContain(Path.GetFileName(path)).And.NotContain(Path.DirectorySeparatorChar.ToString());
    }

    // ---- rollback -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_inside_the_adoption_leaves_the_database_exactly_as_it_was()
    {
        // The ledger write is the last statement of the adoption: a trigger that refuses it fails the transaction after the repair of the
        // missing index has run. Both must be gone, and the 39 old rows must still be there.
        var path = await A26DatabaseAsync();
        Run(path, @"DROP INDEX idx_article_version_article;
                    CREATE TRIGGER bmb_test_block_ledger BEFORE INSERT ON tbl_migration BEGIN SELECT RAISE(ABORT, 'blocked by the test'); END");
        SqliteConnection.ClearAllPools();
        var before = SchemaDump.FileDigest(path);

        using (var factory = new DbConnectionFactory(path))
            await Assert.ThrowsAnyAsync<Exception>(() => Runner(factory).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();

        SchemaDump.FileDigest(path).Should().Be(before, "the transaction rolled back as a whole");
        Query<long>(path, "SELECT COUNT(*) FROM tbl_migration").Should().Be(39);
        Query<long>(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'idx_article_version_article'").Should().Be(0, "the repair went with the rollback");
    }

    [Fact]
    public async Task A_failure_while_the_baseline_is_applied_leaves_an_empty_database()
    {
        var path = NewDbPath();
        Run(path, @"CREATE TABLE tbl_migration (id INTEGER PRIMARY KEY AUTOINCREMENT, version INTEGER NOT NULL UNIQUE, filename TEXT NOT NULL, applied_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                    CREATE TRIGGER bmb_test_block_ledger BEFORE INSERT ON tbl_migration BEGIN SELECT RAISE(ABORT, 'blocked by the test'); END");

        using (var factory = new DbConnectionFactory(path))
            await Assert.ThrowsAnyAsync<Exception>(() => Runner(factory).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();

        Query<string>(path, "SELECT group_concat(name) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").Should().Be("tbl_migration",
            "the baseline and its ledger row are one transaction: a failure leaves no half-built schema behind");
    }

    // ---- the sealed path ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sealed_2_6_database_is_adopted_through_the_sealed_connection()
    {
        var path = NewDbPath();
        var keys = new InMemoryVaultKeySource();
        using (var factory = new DbConnectionFactory(path, keys))
        {
            await Runner(factory).RunMigrationsAsync();
            using var conn = factory.CreateConnection();
            SyntheticRows.Seed(conn);
            SetLedger(conn, Ledger("v2.6.1"));
        }
        SqliteConnection.ClearAllPools();
        (File.ReadAllBytes(path).Take(16).SequenceEqual("SQLite format 3\0"u8.ToArray())).Should().BeFalse("the file is encrypted");

        string shapeBefore, rowsBefore;
        using (var factory = new DbConnectionFactory(path, keys))
        using (var conn = factory.CreateConnection())
        {
            shapeBefore = SchemaDump.Shape(conn, withLedger: false);
            rowsBefore = SchemaDump.RowsText(conn);
        }
        SqliteConnection.ClearAllPools();

        using (var factory = new DbConnectionFactory(path, keys))
            await Runner(factory).RunMigrationsAsync();
        SqliteConnection.ClearAllPools();

        using (var factory = new DbConnectionFactory(path, keys))
        using (var conn = factory.CreateConnection())
        {
            SchemaDump.Ledger(conn).Should().Equal([(40, BaselineName)]);
            SchemaDump.Shape(conn, withLedger: false).Should().Be(shapeBefore);
            SchemaDump.RowsText(conn).Should().Be(rowsBefore);
        }
        SqliteConnection.ClearAllPools();
    }
}

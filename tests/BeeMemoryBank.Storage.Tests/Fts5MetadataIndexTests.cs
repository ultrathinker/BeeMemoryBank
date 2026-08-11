using System.Data;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Tests for migration 004 (FTS5 metadata index). Verifies, at the SQL layer:
///   - trigger sync on INSERT/UPDATE/DELETE for fts_article / fts_folder / fts_tag,
///     including a raw-SQL write path that bypasses every service (the path
///     RemoteEventApplier ultimately takes during sync);
///   - backfill correctness on a pre-populated DB (upgrade simulation);
///   - migration idempotency (re-apply after a ghost-hunter-style reset);
///   - basic MATCH queries returning expected rows with bm25() ranking.
/// Query-side wiring (ArticleRepository/FolderRepository/SearchService) is a
/// separate WP and deliberately not exercised here.
/// </summary>
public class Fts5MetadataIndexTests
{
    private const string Migration004Resource =
        "BeeMemoryBank.Storage.Migrations.004_fts5_metadata_index.sql";

    private static DbConnectionFactory NewFactory(string label) =>
        DbConnectionFactory.CreateInMemory($"bmb_fts5_{label}_{Guid.NewGuid():N}");

    private static async Task MigrateAsync(DbConnectionFactory factory) =>
        await new MigrationRunner(factory).RunMigrationsAsync();

    private static string IsoNow() => DateTime.UtcNow.ToString("o");

    // ---- Article: raw-SQL INSERT path (simulates RemoteEventApplier) --------

    [Fact]
    public async Task Article_RawSqlInsert_IsIndexedByTrigger()
    {
        using var factory = NewFactory("art_ins");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        var id = Guid.NewGuid().ToString();
        var now = IsoNow();
        // Raw INSERT — the path RemoteEventApplier ultimately drives during sync.
        // No service layer involved.
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'Postgres runbook', '/Work/Runbooks/Postgres', 'A', @now, @now)",
            new { id, now });

        var ftsRow = await conn.QuerySingleOrDefaultAsync<(long RowId, string Title, string Path)>(
            "SELECT rowid AS RowId, title AS Title, tree_path AS Path FROM fts_article WHERE fts_article MATCH 'runbook'");
        ftsRow.Title.Should().Be("Postgres runbook");
        ftsRow.Path.Should().Be("/Work/Runbooks/Postgres");

        // rowid in FTS must equal the base table's implicit rowid (JOIN key).
        var baseRowid = await conn.QuerySingleAsync<long>("SELECT rowid FROM tbl_article WHERE id = @id", new { id });
        ftsRow.RowId.Should().Be(baseRowid);
    }

    // ---- Article: UPDATE re-indexes (and tree-path segments tokenize) ------

    [Fact]
    public async Task Article_UpdateOfIndexedColumns_Reindexes()
    {
        using var factory = NewFactory("art_upd");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        var id = Guid.NewGuid().ToString();
        var now = IsoNow();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'Old title', '/Work', 'A', @now, @now)", new { id, now });

        await conn.ExecuteAsync(
            "UPDATE tbl_article SET title = 'New runbook title', updated_at = @now WHERE id = @id",
            new { id, now });

        // Old term gone, new term present.
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'old'"))
            .Should().Be(0);
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'runbook'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Article_UpdateOfUnrelatedColumn_DoesNotReindex()
    {
        using var factory = NewFactory("art_noupd");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        var id = Guid.NewGuid().ToString();
        var now = IsoNow();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'Stable title', '/Work', 'A', @now, @now)", new { id, now });

        // embedding_pending / updated_at change — must not fire the re-index trigger.
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET embedding_pending = 0, updated_at = @now WHERE id = @id",
            new { id, now });

        // Still exactly one index row for this article — no duplicate insert.
        var baseRowid = await conn.QuerySingleAsync<long>("SELECT rowid FROM tbl_article WHERE id = @id", new { id });
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE rowid = @r", new { r = baseRowid }))
            .Should().Be(1);
    }

    // ---- Article: soft-delete does not corrupt the index --------------------

    [Fact]
    public async Task Article_SoftDelete_StatusFlipOnly_DoesNotTouchIndex()
    {
        using var factory = NewFactory("art_soft");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        var id = Guid.NewGuid().ToString();
        var now = IsoNow();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'Soft delete me', '/Work', 'A', @now, @now)", new { id, now });

        await conn.ExecuteAsync(
            "UPDATE tbl_article SET status = 'D', deleted_at = @now WHERE id = @id", new { id, now });

        // Index still reflects the row; query side is responsible for status filtering.
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'soft'"))
            .Should().Be(1);
    }

    // ---- Article: hard DELETE removes from index ----------------------------

    [Fact]
    public async Task Article_HardDelete_RemovesFromIndex()
    {
        using var factory = NewFactory("art_del");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        var id = Guid.NewGuid().ToString();
        var now = IsoNow();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'Delete me', '/Work', 'A', @now, @now)", new { id, now });

        await conn.ExecuteAsync("DELETE FROM tbl_article WHERE id = @id", new { id });

        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'delete'"))
            .Should().Be(0);
    }

    // ---- Article: tree-path tokenization (the WP's design decision) --------

    [Fact]
    public async Task Article_TreePath_SegmentsAreSearchable()
    {
        using var factory = NewFactory("art_path");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();
        var now = IsoNow();

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'T1', '/Work/Runbooks/Postgres', 'A', @now, @now)",
            new { id = Guid.NewGuid().ToString(), now });

        // The default unicode61 tokenizer treats '/' as a separator, so each path
        // segment is its own searchable term — no pre-splitting required. This is
        // the decision the WP report justifies (raw path, no transform).
        foreach (var segment in new[] { "work", "runbooks", "postgres" })
        {
            (await RowCountAsync(conn,
                "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH @term",
                new { term = segment }))
                .Should().Be(1, "path segment '{0}' should be a searchable term", segment);
        }
    }

    // ---- bm25() ranking -----------------------------------------------------

    [Fact]
    public async Task Article_MatchQuery_RanksByBm25()
    {
        using var factory = NewFactory("art_bm25");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();
        var now = IsoNow();

        // Three articles; only two contain the term, with different frequency/context.
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'runbook runbook runbook', '/A', 'A', @now, @now)",
            new { id = "bm25-dense", now });
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'runbook once', '/B', 'A', @now, @now)",
            new { id = "bm25-sparse", now });
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES (@id, 'unrelated', '/C', 'A', @now, @now)",
            new { id = "bm25-none", now });

        // bm25() returns negative scores; more negative = better match. ORDER BY bm25
        // must return the denser article first and must not return the unrelated one.
        var hits = (await conn.QueryAsync<string>(
            "SELECT a.id FROM fts_article f " +
            "JOIN tbl_article a ON a.rowid = f.rowid " +
            "WHERE fts_article MATCH 'runbook' AND a.status = 'A' " +
            "ORDER BY bm25(fts_article)")).ToList();

        hits.Should().HaveCount(2);
        hits.Should().NotContain("bm25-none");
        // Dense (3x) ranks ahead of sparse (1x).
        hits[0].Should().Be("bm25-dense");
        hits[1].Should().Be("bm25-sparse");
    }

    // ---- Folder: INSERT / UPDATE / DELETE sync ------------------------------

    [Fact]
    public async Task Folder_InsertUpdateDelete_StaysInSync()
    {
        using var factory = NewFactory("folder_sync");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();
        var now = IsoNow();

        var id = Guid.NewGuid().ToString();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_folder (id, path, name, parent_path, status, created_at, updated_at) " +
            "VALUES (@id, '/Work/Infra', 'Infra', '/Work', 'A', @now, @now)", new { id, now });

        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_folder WHERE fts_folder MATCH 'infra'"))
            .Should().Be(1);

        await conn.ExecuteAsync(
            "UPDATE tbl_folder SET name = 'Infrastructure', path = '/Work/Infrastructure' WHERE id = @id",
            new { id });

        // FTS5 default MATCH is exact-token: 'infrastructure' is now a distinct token
        // from 'infra'. A prefix query ('infra*') is what resolves the two, and is
        // exactly the mechanism WP-05's stemmer will feed the index from.
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_folder WHERE fts_folder MATCH 'infrastructure'"))
            .Should().Be(1);
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_folder WHERE fts_folder MATCH 'infra*'"))
            .Should().Be(1, "prefix query spans the old 'Infra' token via 'Infrastructure'");
        var title = await conn.QuerySingleAsync<string>(
            "SELECT name FROM fts_folder WHERE fts_folder MATCH 'infrastructure'");
        title.Should().Be("Infrastructure");

        await conn.ExecuteAsync("DELETE FROM tbl_folder WHERE id = @id", new { id });
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_folder WHERE fts_folder MATCH 'infrastructure'"))
            .Should().Be(0);
    }

    // ---- Tag: INSERT / UPDATE / DELETE sync ---------------------------------

    [Fact]
    public async Task Tag_InsertUpdateDelete_StaysInSync()
    {
        using var factory = NewFactory("tag_sync");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        await conn.ExecuteAsync("INSERT INTO tbl_concept_tag (name) VALUES ('dotnet')");

        (await conn.QuerySingleAsync<string>(
            "SELECT name FROM fts_tag WHERE fts_tag MATCH 'dotnet'")).Should().Be("dotnet");

        // Rename: 'delete' old + insert new.
        await conn.ExecuteAsync("UPDATE tbl_concept_tag SET name = 'dotnet-core' WHERE name = 'dotnet'");
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_tag WHERE fts_tag MATCH 'dotnet'"))
            .Should().Be(1, "prefix match against new name");
        (await conn.QuerySingleAsync<string>(
            "SELECT name FROM fts_tag WHERE fts_tag MATCH 'core'")).Should().Be("dotnet-core");

        await conn.ExecuteAsync("DELETE FROM tbl_concept_tag WHERE name = 'dotnet-core'");
        (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_tag WHERE fts_tag MATCH 'core'"))
            .Should().Be(0);
    }

    // ---- Backfill: upgrade of a populated node ------------------------------

    [Fact]
    public async Task Backfill_OnUpgrade_PopulatesIndexFromExistingRows()
    {
        using var factory = NewFactory("backfill");
        // First pass: pretend migration 004 is already applied so the runner only
        // applies 001..003 — this gives us a genuine pre-004 schema to populate.
        await PreCreateMigrationTable(factory);
        await MigrateAsync(factory);

        using (var conn = factory.CreateConnection())
        {
            // Sanity: FTS tables must NOT exist yet (we're at schema v3).
            AssertFtsAbsent(conn, "fts_article");
            AssertFtsAbsent(conn, "fts_folder");
            AssertFtsAbsent(conn, "fts_tag");

            var now = IsoNow();
            // Populate base tables directly. No triggers exist, so FTS is untouched.
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
                "VALUES ('a1', 'Postgres runbook', '/Work/Runbooks/Postgres', 'A', @now, @now)", new { now });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
                "VALUES ('a2', 'Redis cache notes', '/Work/Runbooks/Redis', 'A', @now, @now)", new { now });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_folder (id, path, name, parent_path, status, created_at, updated_at) " +
                "VALUES ('f1', '/Work/Runbooks', 'Runbooks', '/Work', 'A', @now, @now)", new { now });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_concept_tag (name) VALUES ('database')");
            await conn.ExecuteAsync(
                "INSERT INTO tbl_concept_tag (name) VALUES ('caching')");
        }

        // Now drop the v4 marker so the runner applies 004 (the upgrade).
        using (var conn = factory.CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 4");
        }
        await MigrateAsync(factory);

        using (var conn = factory.CreateConnection())
        {
            // All pre-existing rows must be present in the index after backfill.
            (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'runbook'"))
                .Should().Be(1);
            (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'redis'"))
                .Should().Be(1);
            (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_folder WHERE fts_folder MATCH 'runbooks'"))
                .Should().Be(1);
            (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_tag WHERE fts_tag MATCH 'database'"))
                .Should().Be(1);
            (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_tag WHERE fts_tag MATCH 'caching'"))
                .Should().Be(1);

            // Triggers added by 004 are live for future writes.
            var now = IsoNow();
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
                "VALUES ('a3', 'Mongo runbook', '/Work/Runbooks/Mongo', 'A', @now, @now)", new { now });
            (await RowCountAsync(conn, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'mongo'"))
                .Should().Be(1, "post-upgrade trigger must index new writes");
        }
    }

    // ---- Idempotency: re-applying 004 does not error or duplicate -----------

    [Fact]
    public async Task Migration_Reapply_IsIdempotent()
    {
        using var factory = NewFactory("idem");
        await MigrateAsync(factory);
        using var conn = factory.CreateConnection();

        var now = IsoNow();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
            "VALUES ('x', 'idempotency runbook', '/Work', 'A', @now, @now)", new { now });

        // Simulate a ghost-hunter reset: drop the v4 marker so the runner re-runs 004.
        await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version = 4");
        await MigrateAsync(factory); // must not throw, must not duplicate.

        using var conn2 = factory.CreateConnection();
        // CREATE VIRTUAL TABLE / CREATE TRIGGER hit "already exists" (skipped by
        // MigrationRunner's idempotency); 'rebuild' reindexes from the base table.
        (await RowCountAsync(conn2, "SELECT COUNT(*) FROM fts_article WHERE fts_article MATCH 'idempotency'"))
            .Should().Be(1);
        // No duplicate index entries (rebuild discards-then-rebuilds, never appends).
        var baseRowid = await conn2.QuerySingleAsync<long>("SELECT rowid FROM tbl_article WHERE id = 'x'");
        (await RowCountAsync(conn2, "SELECT COUNT(*) FROM fts_article WHERE rowid = @r", new { r = baseRowid }))
            .Should().Be(1);
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>
    /// Creates tbl_migration and marks 004 as already applied (correct resource
    /// name) so the first MigrationRunner pass applies only 001..003 — giving the
    /// backfill test a real pre-004 schema.
    /// </summary>
    private static async Task PreCreateMigrationTable(DbConnectionFactory factory)
    {
        using var conn = factory.CreateConnection();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS tbl_migration (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                version    INTEGER NOT NULL UNIQUE,
                filename   TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )");
        var now = IsoNow();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_migration (version, filename, applied_at, updated_at) " +
            "VALUES (4, @fn, @now, @now)",
            new { fn = Migration004Resource, now });
    }

    private static async Task<int> RowCountAsync(IDbConnection conn, string sql, object? param = null)
        => await conn.QuerySingleAsync<int>(sql, param);

    private static void AssertFtsAbsent(IDbConnection conn, string table)
    {
        var exists = conn.QuerySingle<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @n",
            new { n = table });
        exists.Should().Be(0, "{0} must not exist before migration 004", table);
    }
}

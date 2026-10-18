using System.Data;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Tests for the FTS5 metadata index (migration 005 of the old chain; its objects are part of the baseline now).
/// Verifies, at the SQL layer:
///   - trigger sync on INSERT/UPDATE/DELETE for fts_article / fts_folder / fts_tag,
///     including a raw-SQL write path that bypasses every service (the path
///     RemoteEventApplier ultimately takes during sync);
///   (the rebuild of a missing index from its table is covered by Squash/AdoptionTests);
///   - basic MATCH queries returning expected rows with bm25() ranking.
/// Query-side wiring (ArticleRepository/FolderRepository/SearchService) is a
/// separate WP and deliberately not exercised here.
/// </summary>
public class Fts5MetadataIndexTests
{
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

    // ---- Helpers ------------------------------------------------------------

    private static async Task<int> RowCountAsync(IDbConnection conn, string sql, object? param = null)
        => await conn.QuerySingleAsync<int>(sql, param);

    private static void AssertFtsAbsent(IDbConnection conn, string table)
    {
        var exists = conn.QuerySingle<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @n",
            new { n = table });
        exists.Should().Be(0, "{0} must not exist before migration 005", table);
    }
}

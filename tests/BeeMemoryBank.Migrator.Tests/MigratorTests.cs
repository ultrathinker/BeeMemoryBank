using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Migrator;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Migrator.Tests;

/// <summary>
/// Creates a synthetic v1 SQLite database and verifies that MigratorService correctly migrates data.
/// </summary>
public class MigratorTests : IDisposable
{
    private readonly string _v1DbPath;
    private readonly string _v2DataPath;
    private const string Password = "migrationTest";

    public MigratorTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bmb_migtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _v1DbPath   = Path.Combine(tmp, "v1.db");
        _v2DataPath = Path.Combine(tmp, "v2data");
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_v1DbPath)!;
        if (Directory.Exists(dir))
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // ── Helper methods ────────────────────────────────────────────────

    /// <summary>Creates a minimal v1 database with the specified nodes and articles.</summary>
    private void CreateV1Database(
        IEnumerable<(int Id, int? ParentId, string Name)> nodes,
        IEnumerable<(int Id, int NodeId, string Title, string Content)> articles,
        IEnumerable<(int ArticleId, string Tag)>? tags = null)
    {
        using var conn = new SqliteConnection($"Data Source={_v1DbPath}");
        conn.Open();

        conn.Execute("""
            CREATE TABLE tbl_node (
                id INTEGER PRIMARY KEY,
                parent_id INTEGER,
                name TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'A',
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE tbl_article (
                id INTEGER PRIMARY KEY,
                node_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'A',
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE tbl_tag (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE tbl_article_tag (
                article_id INTEGER NOT NULL,
                tag_id INTEGER NOT NULL,
                PRIMARY KEY (article_id, tag_id)
            );
            """);

        foreach (var (id, parentId, name) in nodes)
            conn.Execute("INSERT INTO tbl_node (id, parent_id, name) VALUES (@id, @parentId, @name)",
                new { id, parentId, name });

        foreach (var (id, nodeId, title, content) in articles)
            conn.Execute("INSERT INTO tbl_article (id, node_id, title, content) VALUES (@id, @nodeId, @title, @content)",
                new { id, nodeId, title, content });

        if (tags != null)
        {
            var tagNames = tags.Select(t => t.Tag).Distinct().ToList();
            foreach (var tag in tagNames)
                conn.Execute("INSERT OR IGNORE INTO tbl_tag (name) VALUES (@name)", new { name = tag });

            foreach (var (articleId, tag) in tags)
                conn.Execute("""
                    INSERT OR IGNORE INTO tbl_article_tag (article_id, tag_id)
                    SELECT @articleId, id FROM tbl_tag WHERE name = @tag
                    """, new { articleId, tag });
        }
    }

    // ── V1Reader tests ────────────────────────────────────────────────────────

    [Fact]
    public void V1Reader_BuildNodePaths_SingleLevel()
    {
        CreateV1Database(
            [(1, null, "Work"), (2, null, "Personal")],
            []
        );

        var reader = new V1Reader(_v1DbPath);
        var nodes  = reader.ReadNodes();
        var paths  = V1Reader.BuildNodePaths(nodes);

        paths[1].Should().Be("/Work");
        paths[2].Should().Be("/Personal");
    }

    [Fact]
    public void V1Reader_BuildNodePaths_Nested()
    {
        CreateV1Database(
            [(1, null, "Work"), (2, 1, "Dev"), (3, 2, "Backend")],
            []
        );

        var reader = new V1Reader(_v1DbPath);
        var nodes  = reader.ReadNodes();
        var paths  = V1Reader.BuildNodePaths(nodes);

        paths[1].Should().Be("/Work");
        paths[2].Should().Be("/Work/Dev");
        paths[3].Should().Be("/Work/Dev/Backend");
    }

    [Fact]
    public void V1Reader_ReadArticles_OnlyActive()
    {
        using var conn = new SqliteConnection($"Data Source={_v1DbPath}");
        conn.Open();
        conn.Execute("""
            CREATE TABLE tbl_node (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'A', created_at TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL DEFAULT '');
            CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, node_id INTEGER, title TEXT, content TEXT, status TEXT NOT NULL DEFAULT 'A', created_at TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL DEFAULT '');
            CREATE TABLE tbl_tag (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT UNIQUE);
            CREATE TABLE tbl_article_tag (article_id INTEGER, tag_id INTEGER, PRIMARY KEY(article_id, tag_id));
            INSERT INTO tbl_node VALUES (1, null, 'Root', 'A', '', '');
            INSERT INTO tbl_article VALUES (1, 1, 'Active', 'text', 'A', '', '');
            INSERT INTO tbl_article VALUES (2, 1, 'Deleted', 'text', 'D', '', '');
            """);

        var reader   = new V1Reader(_v1DbPath);
        var articles = reader.ReadArticles();

        articles.Should().HaveCount(1);
        articles[0].Title.Should().Be("Active");
    }

    [Fact]
    public void V1Reader_ReadTags_ReturnsTagsPerArticle()
    {
        CreateV1Database(
            [(1, null, "Root")],
            [(1, 1, "Article", "text")],
            [(1, "go"), (1, "backend")]
        );

        var reader = new V1Reader(_v1DbPath);
        var tags   = reader.ReadArticleTags();

        tags.Should().ContainKey(1);
        tags[1].Should().BeEquivalentTo(["go", "backend"]);
    }

    // ── MigratorService tests ─────────────────────────────────────────────────

    [Fact]
    public async Task Migrate_SimpleArticles_ArePresentInV2()
    {
        CreateV1Database(
            [(1, null, "Work"), (2, 1, "Dev")],
            [
                (1, 2, "Project Article",    "Content of the first article"),
                (2, 1, "Work Document",        "Content of the second article"),
            ],
            [(1, "go"), (1, "backend")]
        );

        var output = new StringWriter();
        var svc    = new MigratorService(new MigrationOptions(_v1DbPath, _v2DataPath, Password), output);
        var result = await svc.RunAsync();

        result.Migrated.Should().Be(2);
        result.Failed.Should().Be(0);

        // Verify v2 directly
        DapperConfig.Configure();
        var factory = new DbConnectionFactory(_v2DataPath);
        var session = new SessionService(new KeySlotRepository(factory));
        await session.UnlockAsync(Password);

        var articleRepo = new ArticleRepository(factory);
        var bodyRepo    = new ArticleBodyRepository(factory);
        var nodeRepo    = new NodeIdentityRepository(factory);

        var articles = await articleRepo.ListAsync();
        articles.Should().HaveCount(2);
        articles.Select(a => a.Title).Should().BeEquivalentTo(["Project Article", "Work Document"]);

        // Verify tree paths are built correctly
        var devArticle = articles.First(a => a.Title == "Project Article");
        devArticle.TreePath.Should().Be("/Work/Dev");

        var workArticle = articles.First(a => a.Title == "Work Document");
        workArticle.TreePath.Should().Be("/Work");

        // Verify tags
        devArticle.Tags.Should().BeEquivalentTo(["go", "backend"]);

        // Verify content decryption
        var mediaRepo = new MediaRepository(factory);
        var folderRepo = new FolderRepository(factory);
        var versionRepo = new ArticleVersionRepository(factory);
        var articleService = new ArticleService(articleRepo, bodyRepo, session, nodeRepo, new NullLamportClock(), new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider());
        var content = await articleService.GetContentAsync(devArticle.Id);
        content.Should().Be("Content of the first article");
    }

    [Fact]
    public async Task Migrate_DryRun_WritesNothingToV2()
    {
        CreateV1Database(
            [(1, null, "Root")],
            [(1, 1, "Test", "text")]
        );

        var opts   = new MigrationOptions(_v1DbPath, _v2DataPath, Password, DryRun: true);
        var result = await new MigratorService(opts).RunAsync();

        result.Skipped.Should().Be(1);
        result.Migrated.Should().Be(0);

        // v2 data directory does not contain the database
        File.Exists(Path.Combine(_v2DataPath, "beememorybank.db")).Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_EmptyDatabase_Succeeds()
    {
        CreateV1Database([], []);

        var result = await new MigratorService(
            new MigrationOptions(_v1DbPath, _v2DataPath, Password)).RunAsync();

        result.Migrated.Should().Be(0);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Migrate_ArticleWithOrphanNodeId_UsesFallbackPath()
    {
        CreateV1Database(
            [(1, null, "Root")],
            // node_id=999 does not exist
            [(1, 999, "Orphan", "text")]
        );

        var result = await new MigratorService(
            new MigrationOptions(_v1DbPath, _v2DataPath, Password)).RunAsync();

        result.Migrated.Should().Be(1);

        DapperConfig.Configure();
        var factory = new DbConnectionFactory(_v2DataPath);
        var articles = await new ArticleRepository(factory).ListAsync();
        articles[0].TreePath.Should().Be("/Imported");
    }
}

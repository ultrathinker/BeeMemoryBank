using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Verifies database schema through basic Dapper INSERT/SELECT.
/// Goal is to ensure all columns and types are correct (no business logic).
/// </summary>
public class SchemaTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_schema_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TblArticle_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, @title, @path, 'A', @now, @now)",
            new { id, title = "Test", path = "/Work", now });

        var title = await conn.QuerySingleAsync<string>("SELECT title FROM tbl_article WHERE id = @id", new { id });
        title.Should().Be("Test");
    }

    [Fact]
    public async Task TblConceptTag_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("INSERT INTO tbl_concept_tag (name) VALUES (@name)", new { name = "dotnet" });
        var name = await conn.QuerySingleAsync<string>("SELECT name FROM tbl_concept_tag WHERE name = 'dotnet'");
        name.Should().Be("dotnet");
    }

    [Fact]
    public async Task TblArticleConceptTag_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var articleId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'X', '/', 'A', @now, @now)",
            new { id = articleId, now });
        await conn.ExecuteAsync("INSERT INTO tbl_concept_tag (name) VALUES ('tag1')");
        var tagId = await conn.QuerySingleAsync<int>("SELECT id FROM tbl_concept_tag WHERE name = 'tag1'");
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article_concept_tag (article_id, concept_tag_id) VALUES (@articleId, @tagId)",
            new { articleId, tagId });

        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM tbl_article_concept_tag WHERE article_id = @articleId", new { articleId });
        count.Should().Be(1);
    }

    [Fact]
    public async Task TblArticleBody_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var articleId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'X', '/', 'A', @now, @now)",
            new { id = articleId, now });

        var ciphertext = new byte[] { 1, 2, 3 };
        var iv = new byte[] { 4, 5, 6 };
        var encDek = new byte[] { 7, 8, 9 };
        var dekIv = new byte[] { 10, 11, 12 };

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article_body (article_id, ciphertext, iv, encrypted_dek, dek_iv) VALUES (@articleId, @ciphertext, @iv, @encDek, @dekIv)",
            new { articleId, ciphertext, iv, encDek, dekIv });

        var stored = await conn.QuerySingleAsync<byte[]>("SELECT ciphertext FROM tbl_article_body WHERE article_id = @articleId", new { articleId });
        stored.Should().Equal(ciphertext);
    }

    [Fact]
    public async Task TblKeySlot_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
              VALUES ('password', @dek, @iv, @salt, 65536, 3, 1, @now)",
            new { dek = new byte[32], iv = new byte[12], salt = new byte[16], now });

        var count = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = 'password'");
        count.Should().Be(1);
    }

    [Fact]
    public async Task TblNodeIdentity_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var nodeId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, can_generate_embeddings, created_at)
              VALUES (@nodeId, 'Desktop', @pub, @priv, 1, @now)",
            new { nodeId, pub = new byte[32], priv = new byte[64], now });

        var name = await conn.QuerySingleAsync<string>("SELECT display_name FROM tbl_node_identity WHERE node_id = @nodeId", new { nodeId });
        name.Should().Be("Desktop");
    }

    [Fact]
    public async Task TblWhitelist_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var nodeId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, created_at, updated_at)
              VALUES (@nodeId, 'Laptop', @pub, 'A', @now, @now)",
            new { nodeId, pub = new byte[32], now });

        var status = await conn.QuerySingleAsync<string>("SELECT status FROM tbl_whitelist WHERE node_id = @nodeId", new { nodeId });
        status.Should().Be("A");
    }

    [Fact]
    public async Task TblAuditLog_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_audit_log (entity_type, entity_id, action, actor_type, created_at)
              VALUES ('article', @entityId, 'create', 'user', @now)",
            new { entityId = Guid.NewGuid().ToString(), now });

        var count = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_audit_log");
        count.Should().Be(1);
    }

    [Fact]
    public async Task TblConflictVersion_InsertAndSelect()
    {
        using var conn = _factory.CreateConnection();
        var id = Guid.NewGuid().ToString();
        var articleId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");
        var expires = DateTime.UtcNow.AddDays(7).ToString("o");

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@articleId, 'X', '/', 'A', @now, @now)",
            new { articleId, now });

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_conflict_version (id, article_id, source_node_id, lamport_ts, ciphertext, iv, encrypted_dek, dek_iv, created_at, expires_at)
              VALUES (@id, @articleId, @srcNode, 42, @ct, @iv, @dek, @dekIv, @now, @expires)",
            new { id, articleId, srcNode = Guid.NewGuid().ToString(), ct = new byte[16], iv = new byte[12], dek = new byte[32], dekIv = new byte[12], now, expires });

        var lamport = await conn.QuerySingleAsync<long>("SELECT lamport_ts FROM tbl_conflict_version WHERE id = @id", new { id });
        lamport.Should().Be(42);
    }
}

using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

public class MediaRepositoryTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private MediaRepository _repo = null!;
    private CallerScopeHolder _scopeHolder = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory($"bmb_media_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _scopeHolder = new CallerScopeHolder();
        _repo = new MediaRepository(_factory, _scopeHolder);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertOrphanMediaAsync(Guid id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size,
              encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at)
              VALUES (@id, NULL, 'test.png', 'image/png', 100,
              @dek, @dekIv, @iv, 'A', 1, NULL, @now)",
            new { id, dek = new byte[32], dekIv = new byte[12], iv = new byte[12], now = DateTime.UtcNow });
        return id;
    }

    private async Task InsertDummyArticleAsync(Guid articleId)
    {
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at)
              VALUES (@id, 'dummy', '/', 'A', @now, @now)",
            new { id = articleId, now });
    }

    private async Task<Guid> InsertLinkedMediaAsync(Guid id, Guid articleId)
    {
        await InsertDummyArticleAsync(articleId);
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size,
              encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at)
              VALUES (@id, @articleId, 'test.png', 'image/png', 100,
              @dek, @dekIv, @iv, 'A', 1, NULL, @now)",
            new { id, articleId, dek = new byte[32], dekIv = new byte[12], iv = new byte[12], now = DateTime.UtcNow });
        return id;
    }

    private async Task<Guid> InsertDeletedMediaAsync(Guid id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size,
              encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at, deleted_at)
              VALUES (@id, NULL, 'test.png', 'image/png', 100,
              @dek, @dekIv, @iv, 'D', 1, NULL, @now, @now)",
            new { id, dek = new byte[32], dekIv = new byte[12], iv = new byte[12], now = DateTime.UtcNow });
        return id;
    }

    [Fact]
    public async Task LinkOrphansToArticleAsync_LinksOrphanMedia()
    {
        var mediaId = await InsertOrphanMediaAsync(Guid.NewGuid());
        var articleId = Guid.NewGuid();
        await InsertDummyArticleAsync(articleId);

        var linked = await _repo.LinkOrphansToArticleAsync(new[] { mediaId }, articleId, 5, null);

        linked.Should().ContainSingle(id => id == mediaId);
        using var conn = _factory.CreateConnection();
        var linkedArticleId = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT article_id FROM tbl_media WHERE id = @id", new { id = mediaId });
        Guid.Parse(linkedArticleId!).Should().Be(articleId);
    }

    [Fact]
    public async Task LinkOrphansToArticleAsync_SkipsAlreadyLinked()
    {
        var articleId = Guid.NewGuid();
        var otherArticleId = Guid.NewGuid();
        var mediaId = await InsertLinkedMediaAsync(Guid.NewGuid(), otherArticleId);

        await InsertDummyArticleAsync(articleId);
        var linked = await _repo.LinkOrphansToArticleAsync(new[] { mediaId }, articleId, 5, null);

        linked.Should().BeEmpty();
    }

    [Fact]
    public async Task LinkOrphansToArticleAsync_SkipsDeletedMedia()
    {
        var mediaId = await InsertDeletedMediaAsync(Guid.NewGuid());
        var articleId = Guid.NewGuid();
        await InsertDummyArticleAsync(articleId);

        var linked = await _repo.LinkOrphansToArticleAsync(new[] { mediaId }, articleId, 5, null);

        linked.Should().BeEmpty();
    }

    [Fact]
    public async Task LinkOrphansToArticleAsync_ReturnsOnlyLinkedIds()
    {
        var orphanId = await InsertOrphanMediaAsync(Guid.NewGuid());
        var deletedId = await InsertDeletedMediaAsync(Guid.NewGuid());
        var otherArticleId = Guid.NewGuid();
        var linkedId = await InsertLinkedMediaAsync(Guid.NewGuid(), otherArticleId);
        var articleId = Guid.NewGuid();
        await InsertDummyArticleAsync(articleId);

        var linked = await _repo.LinkOrphansToArticleAsync(new[] { orphanId, deletedId, linkedId }, articleId, 5, null);

        linked.Should().ContainSingle(id => id == orphanId);
    }

    [Fact]
    public async Task LinkOrphansToArticleAsync_UpdatesLamportTs()
    {
        var mediaId = await InsertOrphanMediaAsync(Guid.NewGuid());
        var articleId = Guid.NewGuid();
        await InsertDummyArticleAsync(articleId);

        await _repo.LinkOrphansToArticleAsync(new[] { mediaId }, articleId, 42, Guid.NewGuid());

        using var conn = _factory.CreateConnection();
        var lamport = await conn.QuerySingleAsync<long>(
            "SELECT lamport_ts FROM tbl_media WHERE id = @id", new { id = mediaId });
        lamport.Should().Be(42);
    }
}

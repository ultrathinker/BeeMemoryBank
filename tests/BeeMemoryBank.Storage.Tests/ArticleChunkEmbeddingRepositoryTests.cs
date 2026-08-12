using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>WP-15: correctness tests for tbl_article_chunk_embedding's repository.</summary>
public class ArticleChunkEmbeddingRepositoryTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private ArticleChunkEmbeddingRepository _repo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_chunk_embedding_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _repo = new ArticleChunkEmbeddingRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertArticleAsync(string status = "A")
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'x', '/', @status, @now, @now)",
            new { id, status, now });
        return id;
    }

    private static (byte[] Projection, float Scale) FakeChunk(byte fill) => ([fill, fill, fill], 0.01f);

    [Fact]
    public async Task ReplaceChunksAsync_ThenGetAll_ReturnsInsertedRows()
    {
        var articleId = await InsertArticleAsync();
        var chunks = new List<(byte[], float)> { FakeChunk(1), FakeChunk(2), FakeChunk(3) };

        await _repo.ReplaceChunksAsync(articleId, chunks, "minilm-l6-v2");

        var rows = await _repo.GetAllForActiveArticlesAsync();
        rows.Should().HaveCount(3);
        rows.Select(r => r.ChunkIndex).Should().Equal(0, 1, 2);
        rows.Should().OnlyContain(r => r.ArticleId == articleId);
    }

    [Fact]
    public async Task ReplaceChunksAsync_CalledTwice_DropsOldChunksInsteadOfAccumulating()
    {
        var articleId = await InsertArticleAsync();
        await _repo.ReplaceChunksAsync(articleId, [FakeChunk(1), FakeChunk(2)], "minilm-l6-v2");

        // Re-embedding after an edit (fewer chunks the second time) must not leave the old, now
        // stale, third-and-fourth chunk rows behind.
        await _repo.ReplaceChunksAsync(articleId, [FakeChunk(9)], "minilm-l6-v2");

        var rows = await _repo.GetAllForActiveArticlesAsync();
        rows.Should().HaveCount(1);
        rows[0].ChunkIndex.Should().Be(0);
        rows[0].Projection.Should().Equal(FakeChunk(9).Projection);
    }

    [Fact]
    public async Task ReplaceChunksAsync_EmptyList_LeavesNoRows()
    {
        var articleId = await InsertArticleAsync();
        await _repo.ReplaceChunksAsync(articleId, [FakeChunk(1)], "minilm-l6-v2");

        // A protected article, or one whose chunker produced nothing, replaces with zero chunks.
        await _repo.ReplaceChunksAsync(articleId, [], "minilm-l6-v2");

        var rows = await _repo.GetAllForActiveArticlesAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllForActiveArticlesAsync_ExcludesSoftDeletedArticles()
    {
        var activeId = await InsertArticleAsync(status: "A");
        var deletedId = await InsertArticleAsync(status: "D");
        await _repo.ReplaceChunksAsync(activeId, [FakeChunk(1)], "minilm-l6-v2");
        await _repo.ReplaceChunksAsync(deletedId, [FakeChunk(2)], "minilm-l6-v2");

        var rows = await _repo.GetAllForActiveArticlesAsync();

        rows.Should().ContainSingle().Which.ArticleId.Should().Be(activeId);
    }

    [Fact]
    public async Task HardDeletingArticle_CascadesToChunkRows()
    {
        var articleId = await InsertArticleAsync();
        await _repo.ReplaceChunksAsync(articleId, [FakeChunk(1), FakeChunk(2)], "minilm-l6-v2");

        using (var conn = _factory.CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_article WHERE id = @articleId", new { articleId });
        }

        using var check = _factory.CreateConnection();
        var remaining = await check.QueryAsync<int>(
            "SELECT COUNT(*) FROM tbl_article_chunk_embedding WHERE article_id = @articleId", new { articleId });
        remaining.Single().Should().Be(0);
    }
}

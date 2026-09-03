using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// WP-15: durable storage for tbl_article_chunk_embedding (one int8-quantized projection vector
/// per ~256-token chunk of an article — see <c>BeeMemoryBank.Core.Embeddings.ArticleChunker</c>).
/// A sibling table to tbl_article's single <c>embedding_projection</c> column, not a replacement
/// for it: an article with no rows here yet (not (re)chunked since WP-15 shipped) still has its
/// old full-document embedding as a fallback — see <see cref="ChunkEmbeddingVectorCache"/>.
/// </summary>
public sealed class ArticleChunkEmbeddingRepository(DbConnectionFactory factory, ChunkEmbeddingVectorCache? cache = null)
    : BaseRepository(factory), IArticleChunkEmbeddingRepository
{
    /// <summary>
    /// Replaces every chunk row for <paramref name="articleId"/> with <paramref name="chunks"/>,
    /// atomically (delete-then-insert in one transaction). Called on every
    /// <c>EmbeddingProjectionService.ProjectArticleAsync</c> run for that article, exactly like
    /// <c>ArticleRepository.UpdateEmbeddingUnscopedAsync</c> rewrites the single full-document projection —
    /// so re-embedding an edited article naturally drops stale chunks from its previous content
    /// instead of accumulating them.
    /// </summary>
    public async Task ReplaceChunksAsync(Guid articleId, IReadOnlyList<(byte[] Projection, float Scale)> chunks, string modelVersion)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM tbl_article_chunk_embedding WHERE article_id = @articleId",
            new { articleId }, tx);

        if (chunks.Count > 0)
        {
            var rows = chunks.Select((c, i) => new
            {
                articleId,
                chunkIndex = i,
                projection = c.Projection,
                scale = c.Scale,
                modelVersion
            });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article_chunk_embedding (article_id, chunk_index, projection, scale, model_version)
                  VALUES (@articleId, @chunkIndex, @projection, @scale, @modelVersion)",
                rows, tx);
        }

        tx.Commit();

        // WP-15: this is the one write path that changes chunk-embedding bytes during normal
        // operation (PendingEmbeddingProcessor via EmbeddingProjectionService). Mirrors
        // ArticleRepository.UpdateEmbeddingUnscopedAsync's own EmbeddingVectorCache.Invalidate() call.
        cache?.Invalidate();
    }

    /// <summary>
    /// Every chunk row belonging to a currently-active article, for
    /// <see cref="ChunkEmbeddingVectorCache"/>'s full rebuild. Soft-deleted articles' chunk rows
    /// (orphaned until a hard delete's FK cascade actually removes them) are excluded via the join,
    /// the same way <c>EmbeddingVectorCache</c> filters on <c>a.status = 'A'</c>.
    /// </summary>
    public async Task<List<ArticleChunkEmbeddingRow>> GetAllForActiveArticlesAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<RawRow>(
            @"SELECT c.article_id AS ArticleId, c.chunk_index AS ChunkIndex,
                     c.projection AS Projection, c.scale AS Scale
              FROM tbl_article_chunk_embedding c
              JOIN tbl_article a ON a.id = c.article_id
              WHERE a.status = 'A'
              ORDER BY c.article_id, c.chunk_index");

        return rows.Select(r => new ArticleChunkEmbeddingRow(r.ArticleId, r.ChunkIndex, r.Projection, r.Scale)).ToList();
    }

    // Dapper binds by alias; querying straight into ArticleChunkEmbeddingRow's constructor would
    // rely on record-constructor binding this codebase doesn't otherwise use (every other repo here
    // maps through an explicit mutable DTO instead) -- kept consistent with that pattern.
    private sealed class RawRow
    {
        public Guid ArticleId { get; set; }
        public int ChunkIndex { get; set; }
        public byte[] Projection { get; set; } = null!;
        public float Scale { get; set; }
    }
}

using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>WP-15: durable storage for one article's per-chunk semantic embeddings.</summary>
public interface IArticleChunkEmbeddingRepository
{
    /// <summary>
    /// Replaces every chunk row for <paramref name="articleId"/> with <paramref name="chunks"/>,
    /// atomically. Pass an empty list to clear an article's chunks entirely (e.g. a protected
    /// article, mirroring how <c>ArticleRepository.UpdateEmbeddingAsync</c> stores an empty
    /// full-document projection for the same case).
    /// </summary>
    Task ReplaceChunksAsync(Guid articleId, IReadOnlyList<(byte[] Projection, float Scale)> chunks, string modelVersion);

    /// <summary>Every chunk row belonging to a currently-active article.</summary>
    Task<List<ArticleChunkEmbeddingRow>> GetAllForActiveArticlesAsync();
}

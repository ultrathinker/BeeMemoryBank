using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IConceptTagRepository
{
    Task<List<ConceptTagInfo>> GetAllAsync();
    Task<List<string>> GetByArticleIdAsync(Guid articleId);
    Task<Dictionary<Guid, List<string>>> GetByArticleIdsAsync(IEnumerable<Guid> articleIds);
    Task SetForArticleAsync(Guid articleId, List<string> conceptNames);
    Task<List<RelatedArticle>> GetRelatedArticlesAsync(Guid articleId);
    Task<List<(Guid Id, string Title, string TreePath)>> SearchByConceptAsync(string concept);

    // Phase 2 methods
    Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit);
    Task<List<ConceptTagWithEmbedding>> GetWithEmbeddingsAsync();
    Task<List<ConceptGraphEdge>> GetGraphDataAsync();
    Task<List<ConceptGraphEdge>> GetNeighborGraphAsync(string tag);
    Task AddToArticleAsync(Guid articleId, List<string> conceptNames);
    Task RemoveFromArticleAsync(Guid articleId, string conceptName);
    Task RenameAsync(string name, string newName);
    Task MergeAsync(string source, string target);
    Task DeleteAsync(string name);
    Task UpdateEmbeddingAsync(string name, byte[] embedding, string modelVersion);

    Task<ConceptTagGraphData> GetHomeGraphAsync();
    Task<ConceptTagGraphData> SearchGraphAsync(string query, int depth, int maxNodes);
    Task<ConceptTagEdgeStats> GetEdgeStatsAsync();
    Task<ConceptTagEdgeRebuildReport> CheckAndRebuildEdgesAsync();
}

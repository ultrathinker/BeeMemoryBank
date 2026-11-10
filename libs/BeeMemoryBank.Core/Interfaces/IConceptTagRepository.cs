using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IConceptTagRepository
{
    Task<List<ConceptTagInfo>> GetAllAsync();
    Task<List<string>> GetByArticleIdAsync(Guid articleId, IDbTransaction? transaction = null);
    Task<Dictionary<Guid, List<string>>> GetByArticleIdsAsync(IEnumerable<Guid> articleIds);
    Task SetForArticleAsync(Guid articleId, List<string> conceptNames, IDbTransaction? transaction = null);

    /// <summary>
    /// The articles most related to <paramref name="articleId"/>, strongest first, at most <paramref name="limit"/>
    /// (clamped to 1..<see cref="RelatedArticle.MaxLimit"/>), restricted to what the caller may see.
    /// Shared tags count by rarity and nearness by meaning counts too: see <see cref="RelatedArticle"/>.
    /// </summary>
    /// <remarks>
    /// The article vectors are derived from article bodies and sit in the database in the clear, so a caller that
    /// serves a request while the vault is locked passes <c>includeMeaning: false</c>: the vector snapshot is then
    /// not read at all and the list is made of the tag part alone (the same rule as ranked search, which is skipped
    /// while locked). The default is true for callers that have no lock to honour (the phone, which has no vectors).
    /// </remarks>
    Task<List<RelatedArticle>> GetRelatedArticlesAsync(Guid articleId, int limit = RelatedArticle.DefaultLimit, bool includeMeaning = true);

    /// <summary>
    /// Cheap summary for article metadata: how many visible articles share at least one rare tag with
    /// <paramref name="articleId"/>, and how many rare tags they share in total. Reads tags only, never vectors.
    /// </summary>
    Task<(int Count, int SharedTagTotal)> GetRelatedTagSummaryAsync(Guid articleId);

    Task<List<(Guid Id, string Title, string TreePath)>> SearchByConceptAsync(string concept);

    // Tag management, embeddings and graph
    Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit, int offset = 0);
    Task<List<ConceptTagWithEmbedding>> GetWithEmbeddingsAsync();
    Task<List<ConceptGraphEdge>> GetGraphDataAsync();
    Task<List<ConceptGraphEdge>> GetNeighborGraphAsync(string tag);
    Task RenameAsync(string name, string newName);
    Task MergeAsync(string source, string target);
    Task DeleteAsync(string name);
    Task UpdateEmbeddingAsync(string name, byte[] embedding, string modelVersion, IDbTransaction? transaction = null);

    Task<ConceptTagGraphData> GetHomeGraphAsync();
    Task<ConceptTagGraphData> SearchGraphAsync(string query, int depth, int maxNodes, string? treePath = null);
    Task<ConceptTagEdgeStats> GetEdgeStatsAsync();
    Task<ConceptTagEdgeRebuildReport> CheckAndRebuildEdgesAsync();
}

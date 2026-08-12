using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>Which ranking source(s) <see cref="HybridSearchService.SearchAsync"/> should use.</summary>
public enum SearchMode
{
    Keyword,
    Semantic,
    Hybrid
}

/// <summary>
/// WP-16: combines WP-08–13's BM25 keyword ranking (<see cref="SearchService.SearchIndexedContentAsync"/>)
/// and WP-15's chunk-based semantic ranking (<see cref="IArticleRepository.SearchByChunkEmbeddingAsync"/>)
/// via <see cref="ReciprocalRankFusion"/>, so a query benefits from both exact-term matching and
/// semantic similarity without one drowning out the other.
///
/// <para>
/// Kept as its own class rather than added to <see cref="SearchService"/> directly: every caller of
/// the widely-shared, unconditionally-registered <see cref="SearchService"/> would otherwise need
/// <see cref="EmbeddingProjectionService"/> to resolve too, even callers that only ever do keyword
/// search — <see cref="EmbeddingProjectionService"/> requires <c>IEmbeddingGenerator</c>, which is
/// registered by a separate, optional <c>AddOnnxEmbeddings</c> call some hosts (CLI/tools) never
/// make. A dedicated, narrowly-scoped class keeps that dependency opt-in.
/// </para>
/// </summary>
public class HybridSearchService(
    SearchService searchService,
    IArticleRepository articleRepo,
    EmbeddingProjectionService embeddingProjection,
    SessionService session)
{
    /// <summary>
    /// Fetch width used for each individual ranking source before fusing them, when
    /// <paramref name="topK"/> (below) is small — RRF needs headroom beyond the final result count
    /// so a candidate ranked outside the top few by one source, but highly by the other, still has
    /// a chance to surface after fusion.
    /// </summary>
    private const int MinFetchWidth = 50;

    public async Task<List<Article>> SearchAsync(string query, SearchMode mode, int topK = 20)
    {
        return mode switch
        {
            SearchMode.Keyword => await searchService.SearchIndexedContentAsync(query, topK),
            SearchMode.Semantic => await SemanticOnlyAsync(query, topK),
            SearchMode.Hybrid => await HybridAsync(query, topK),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown search mode."),
        };
    }

    private async Task<List<Article>> SemanticOnlyAsync(string query, int topK)
    {
        RequireUnlocked();
        float[] projection = await embeddingProjection.ProjectQueryAsync(query);
        return await articleRepo.SearchByChunkEmbeddingAsync(projection, topK);
    }

    private async Task<List<Article>> HybridAsync(string query, int topK)
    {
        RequireUnlocked();

        int fetchWidth = Math.Max(topK * 4, MinFetchWidth);

        Task<List<Article>> keywordTask = searchService.SearchIndexedContentAsync(query, fetchWidth);

        List<Article> semanticResults;
        try
        {
            float[] projection = await embeddingProjection.ProjectQueryAsync(query);
            semanticResults = await articleRepo.SearchByChunkEmbeddingAsync(projection, fetchWidth);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ModelUnavailableException)
        {
            // Semantic search isn't available for this call -- e.g. this node never generates
            // embeddings (tbl_node_identity.can_generate_embeddings = 0 in a multi-node sync setup)
            // so the projection matrix was never initialized here, or the ONNX model itself is
            // unavailable. Degrade to keyword-only ranking rather than failing the entire hybrid
            // request: BM25 search works independently of embeddings and should not be held hostage
            // by a missing semantic component. Found in production (2026-08-12): a node without
            // embedding generation got zero hybrid results (not even its keyword-only results)
            // before this fix, silently falling all the way back to the pre-WP-16 linear body scan.
            semanticResults = [];
        }

        List<Article> keywordResults = await keywordTask;

        var byId = new Dictionary<Guid, Article>();
        foreach (Article a in keywordResults) byId[a.Id] = a;
        foreach (Article a in semanticResults) byId.TryAdd(a.Id, a);

        List<Guid> combinedIds = ReciprocalRankFusion.Combine(
            [keywordResults.Select(a => a.Id).ToList(), semanticResults.Select(a => a.Id).ToList()],
            topK);

        return combinedIds.Select(id => byId[id]).ToList();
    }

    private void RequireUnlocked()
    {
        if (!session.IsUnlocked)
        {
            throw new InvalidOperationException("Session is locked.");
        }
    }
}

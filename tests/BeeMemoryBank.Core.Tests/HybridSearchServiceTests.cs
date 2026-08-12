using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// WP-16: <see cref="HybridSearchService"/> integration tests — keyword-only and semantic-only
/// modes each defer entirely to their single underlying source, and hybrid mode surfaces an article
/// that literal keyword search cannot find at all (because it uses a synonym instead of the exact
/// query word) via its semantic contribution — the actual value proposition of hybrid search.
///
/// <para>
/// Uses a small fake <see cref="IEmbeddingGenerator"/> that treats <c>"urgent"</c> and its synonym
/// <c>"pressing"</c> as embedding to the same direction, simulating (crudely, but deterministically)
/// the synonym/paraphrase understanding a real embedding model provides over literal keyword
/// matching — which this codebase's BM25/FTS search, being exact/stemmed rather than semantic,
/// cannot do at all.
/// </para>
/// </summary>
public class HybridSearchServiceTests : IAsyncLifetime
{
    private const string KeywordTerm = "urgentXYZ";
    private const string SemanticSynonym = "pressingXYZ";

    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private CallerScopeHolder _scopeHolder = null!;
    private IndexBuilder _indexBuilder = null!;
    private ArticleRepository _articleRepo = null!;
    private HybridSearchService _hybridSearch = null!;
    private SearchService _searchService = null!;
    private EmbeddingProjectionService _projectionService = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_hybrid_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var keySlotRepo = new KeySlotRepository(_factory);
        _session = new SessionService(keySlotRepo);
        _session.UnlockWithDek(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _scopeHolder = new CallerScopeHolder();
        var vectorCache = new EmbeddingVectorCache(_factory);
        var chunkCache = new ChunkEmbeddingVectorCache(_factory);
        _articleRepo = new ArticleRepository(_factory, _scopeHolder, vectorCache, searchMetrics: null, chunkCache);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var folderRepo = new FolderRepository(_factory, _scopeHolder);
        var queryCache = new SearchQueryCache();
        _indexBuilder = new IndexBuilder();

        _searchService = new SearchService(_articleRepo, bodyRepo, folderRepo, _session, _scopeHolder, queryCache, _indexBuilder);

        var matrixRepo = new ProjectionMatrixRepository(_factory);
        var chunkRepo = new ArticleChunkEmbeddingRepository(_factory, chunkCache);
        var chunker = ArticleChunker.CreateDefault();
        var generator = new SynonymAwareEmbeddingGenerator();
        _projectionService = new EmbeddingProjectionService(generator, matrixRepo, _articleRepo, _session, chunker, chunkRepo);
        await _projectionService.EnsureProjectionMatrixAsync();

        _hybridSearch = new HybridSearchService(_searchService, _articleRepo, _projectionService, _session);
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Article> InsertArticleAsync(string title, string content)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, @title, '/', 'A', @now, @now)",
            new { id, title, now });

        var article = new Article { Id = id, Title = title };
        _indexBuilder.AddOrUpdateDocument(id, Guid.Empty, content);
        await _projectionService.ProjectArticleAsync(article, content);
        return article;
    }

    [Fact]
    public async Task SearchAsync_KeywordMode_FindsLiteralTermViaBm25()
    {
        var literalMatch = await InsertArticleAsync("Literal", $"{KeywordTerm} {KeywordTerm} filler");

        var results = await _hybridSearch.SearchAsync(KeywordTerm, SearchMode.Keyword, topK: 10);

        results.Should().ContainSingle().Which.Id.Should().Be(literalMatch.Id);
    }

    [Fact]
    public async Task SearchAsync_SemanticMode_FindsSynonymEvenWithoutLiteralTerm()
    {
        // Contains the synonym, never the literal keyword.
        var synonymOnly = await InsertArticleAsync("Synonym", $"{SemanticSynonym} deadline tomorrow");
        var unrelated = await InsertArticleAsync("Unrelated", "completely different topic entirely");

        // topK=1: with only 2 candidates, a larger topK would return both regardless of score
        // (there is no minimum-score threshold), which would make this assertion vacuous.
        var results = await _hybridSearch.SearchAsync(KeywordTerm, SearchMode.Semantic, topK: 1);

        results.Should().ContainSingle().Which.Id.Should().Be(synonymOnly.Id,
            "the synonym must be recognized as semantically equivalent to the literal query term");
        results.Should().NotContain(a => a.Id == unrelated.Id);
    }

    [Fact]
    public async Task SearchAsync_HybridMode_FindsSynonymArticle_ThatKeywordSearchCannotFindAtAll()
    {
        var synonymOnly = await InsertArticleAsync("Synonym", $"{SemanticSynonym} deadline tomorrow");

        // The literal keyword search must find NOTHING at all for this query -- proof the synonym
        // article is genuinely invisible to it, not just ranked lower.
        var keywordOnlyResults = await _searchService.SearchIndexedContentAsync(KeywordTerm, topK: 10);
        keywordOnlyResults.Should().BeEmpty();

        var hybridResults = await _hybridSearch.SearchAsync(KeywordTerm, SearchMode.Hybrid, topK: 10);

        hybridResults.Should().Contain(a => a.Id == synonymOnly.Id,
            "hybrid mode must surface the article via its semantic contribution even though the keyword source alone finds nothing");
    }

    [Fact]
    public async Task SearchAsync_HybridMode_CombinesBothSources_WhenBothMatch()
    {
        var literalMatch = await InsertArticleAsync("Literal", $"{KeywordTerm} {KeywordTerm} filler");
        var synonymMatch = await InsertArticleAsync("Synonym", $"{SemanticSynonym} deadline tomorrow");

        var results = await _hybridSearch.SearchAsync(KeywordTerm, SearchMode.Hybrid, topK: 10);

        results.Select(a => a.Id).Should().Contain([literalMatch.Id, synonymMatch.Id],
            "both the literal-keyword match and the synonym-only match should surface under hybrid mode");
    }

    [Fact]
    public async Task SearchAsync_HybridMode_LockedSession_Throws()
    {
        await InsertArticleAsync("X", "content");
        _session.Lock();

        var act = () => _hybridSearch.SearchAsync("content", SearchMode.Hybrid, topK: 10);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // Treats the literal keyword and its "synonym" as embedding to the same direction -- a crude,
    // deterministic stand-in for the synonym/paraphrase understanding a real embedding model
    // provides, which this codebase's literal/stemmed BM25 search cannot do at all. Neither word
    // appearing embeds to the opposite direction.
    private sealed class SynonymAwareEmbeddingGenerator : IEmbeddingGenerator
    {
        public int Dimension => 2;

        public float[] Generate(string text) =>
            text.Contains(KeywordTerm, StringComparison.OrdinalIgnoreCase) ||
            text.Contains(SemanticSynonym, StringComparison.OrdinalIgnoreCase)
                ? [1f, 0f]
                : [0f, 1f];
    }
}

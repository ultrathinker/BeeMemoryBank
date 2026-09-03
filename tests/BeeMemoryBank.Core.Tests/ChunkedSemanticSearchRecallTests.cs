using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// WP-15's headline DoD: on a synthetic "needle in a long article" corpus, chunk-based semantic
/// search (<see cref="ArticleRepository.SearchByChunkEmbeddingAsync"/>) must surface an article
/// whose distinctive content sits past <see cref="OnnxEmbeddingGenerator.MaxSequenceLength"/>
/// tokens, while the pre-WP-15 single-embedding-per-article search
/// (<see cref="ArticleRepository.SearchByEmbeddingAsync"/>) does not — proving chunking actually
/// fixes the truncation blind spot, not just that it runs without error.
///
/// <para>
/// Uses a small fake <see cref="IEmbeddingGenerator"/> instead of the real ONNX model — consistent
/// with every other embedding-related test in this codebase (no test here runs real ONNX
/// inference; see <c>OnnxEmbeddingGeneratorTests</c>). The fake still reuses the REAL
/// <see cref="XlmRobertaTokenizer"/> (via <c>InternalsVisibleTo</c>) with the exact same
/// <see cref="OnnxEmbeddingGenerator.MaxSequenceLength"/> truncation the real model applies, so the
/// truncation behavior this test proves chunking fixes is the genuine article, not a simplified
/// stand-in for it. It reports "found the needle" (embedding [1,0]) vs "did not" ([0,1]) based on
/// whether the needle's own SentencePiece token ids survived that truncation — a deterministic,
/// instant proxy for "a real embedding model would have picked up this content," which is exactly
/// the property truncation removes and chunking restores.
/// </para>
/// </summary>
public class ChunkedSemanticSearchRecallTests : IAsyncLifetime
{
    private const string NeedleMarker = "needlemarker9f3a";

    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleRepository _articleRepo = null!;
    private EmbeddingProjectionService _projectionService = null!;
    private EmbeddingVectorCache _vectorCache = null!;
    private ChunkEmbeddingVectorCache _chunkCache = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_chunk_recall_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var keySlotRepo = new KeySlotRepository(_factory);
        _session = new SessionService(keySlotRepo);
        _session.UnlockWithDek(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        var scopeHolder = new CallerScopeHolder();
        _vectorCache = new EmbeddingVectorCache(_factory);
        _chunkCache = new ChunkEmbeddingVectorCache(_factory);
        var chunkCache = _chunkCache;
        _articleRepo = new ArticleRepository(_factory, scopeHolder, _vectorCache, searchMetrics: null, chunkCache);

        var matrixRepo = new ProjectionMatrixRepository(_factory);
        var chunkRepo = new ArticleChunkEmbeddingRepository(_factory, chunkCache);
        var generator = new TruncationAwareFakeGenerator();
        var chunker = ArticleChunker.CreateDefault();

        _projectionService = new EmbeddingProjectionService(generator, matrixRepo, _articleRepo, _session, chunker, chunkRepo);
        await _projectionService.EnsureProjectionMatrixAsync();
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Article> InsertArticleAsync(string title)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, @title, '/', 'A', @now, @now)",
            new { id, title, now });
        return new Article { Id = id, Title = title };
    }

    private static string LongArticleWithNeedleNearEnd()
    {
        // Comfortably more than ArticleChunker.ChunkTokenBudget words of filler before the needle,
        // so the needle sits well past the single-embedding truncation point.
        var filler = string.Join(' ', Enumerable.Range(0, 1500).Select(i => $"filler{i}"));
        return $"{filler} {NeedleMarker} {filler}";
    }

    [Fact]
    public async Task ChunkBasedSearch_FindsNeedleBeyondTruncationPoint_FullDocumentSearchDoesNot()
    {
        var haystack = await InsertArticleAsync("Haystack");
        await _projectionService.ProjectArticleAsync(haystack, LongArticleWithNeedleNearEnd());

        // A handful of unrelated short articles so this isn't a trivial "only one candidate exists" test.
        for (int i = 0; i < 3; i++)
        {
            var decoy = await InsertArticleAsync($"Decoy {i}");
            await _projectionService.ProjectArticleAsync(decoy, $"unrelated short content {i}");
        }

        var query = await _projectionService.ProjectQueryAsync(NeedleMarker);

        // Check the RAW cosine score directly rather than "is it in the top-K list": with only 4
        // candidates total and topK=10, every candidate would appear in a top-K list regardless of
        // score, which would make a membership assertion vacuous. The score is the real claim.
        var oldScore = _vectorCache.GetOrRebuild().ScoreAll(query).GetValueOrDefault(haystack.Id, -1f);
        var newScore = (await _chunkCache.GetOrRebuildAsync()).ScoreMaxPerArticle(query).GetValueOrDefault(haystack.Id, -1f);

        oldScore.Should().BeApproximately(0f, 1e-4f,
            "the pre-WP-15 single full-document embedding truncates before the needle, so its cosine score against a needle-only query must be ~0 -- this pins the baseline bug WP-15 exists to fix");
        newScore.Should().BeApproximately(1f, 1e-4f,
            "the needle's own chunk should score a near-perfect cosine match against the needle-only query");

        var newResults = await _articleRepo.SearchByChunkEmbeddingAsync(query, topK: 10);
        newResults.Should().Contain(a => a.Id == haystack.Id,
            "chunk-based search must find the needle via the chunk that actually contains it, proving recall improved over the baseline");
        newResults[0].Id.Should().Be(haystack.Id, "the needle's own chunk should score highest, ranking the haystack article first");
    }

    [Fact]
    public async Task ChunkBasedSearch_ArticleWithoutChunksYet_FallsBackToFullDocumentScore()
    {
        // Simulates an article embedded before WP-15 shipped (or never re-chunked): a full-document
        // projection exists, but tbl_article_chunk_embedding has no rows for it.
        var article = await InsertArticleAsync("Pre-WP-15 article");
        var embedding = new TruncationAwareFakeGenerator().Generate(NeedleMarker);
        var matrixRepo = new ProjectionMatrixRepository(_factory);
        var stored = await matrixRepo.GetAsync();
        stored.Should().NotBeNull();

        // Write only the full-document projection directly (bypassing ProjectArticleAsync, which
        // would also populate chunks) to reproduce the "not yet backfilled" state precisely.
        var masterDek = _session.GetMasterDek();
        var matrix = ProjectionMatrix.Unwrap(stored!.EncryptedMatrix, stored.IV, masterDek);
        var projection = matrix.Project(embedding);
        var bytes = new byte[projection.Length * 4];
        Buffer.BlockCopy(projection, 0, bytes, 0, bytes.Length);
        await _articleRepo.UpdateEmbeddingUnscopedAsync(article.Id, bytes, "test-model");

        var query = await _projectionService.ProjectQueryAsync(NeedleMarker);
        var results = await _articleRepo.SearchByChunkEmbeddingAsync(query, topK: 10);

        results.Should().Contain(a => a.Id == article.Id,
            "an article with no chunk rows yet must still be findable via its full-document embedding fallback");
    }

    // Reuses the REAL XlmRobertaTokenizer (internal, visible via InternalsVisibleTo) so the
    // truncation this fake simulates is byte-for-byte the same truncation OnnxEmbeddingGenerator
    // itself applies -- only the final "which direction does this embedding point" step is faked.
    private sealed class TruncationAwareFakeGenerator : IEmbeddingGenerator
    {
        private static readonly XlmRobertaTokenizer Tokenizer = XlmRobertaTokenizer.LoadDefault();

        public int Dimension => 2;

        public float[] Generate(string text)
        {
            var (inputIds, _, _) = Tokenizer.Encode(text, OnnxEmbeddingGenerator.MaxSequenceLength);
            var (needleIds, _, _) = Tokenizer.Encode(NeedleMarker, OnnxEmbeddingGenerator.MaxSequenceLength);

            // Strip [BOS]/[EOS] to get just the needle's own content token ids, in order.
            var needleContentIds = needleIds.Skip(1).Take(needleIds.Length - 2).ToArray();

            // Exact contiguous subsequence match, not "all these ids appear somewhere" -- the
            // needle's own SentencePiece breakdown can include common single-character/digit pieces
            // (e.g. a piece shared with "filler123"-style filler words), which a scattered-membership
            // check would false-positive on long filler text. A contiguous run is what "the needle
            // text actually appears here" really means.
            bool found = needleContentIds.Length > 0 && ContainsSubsequence(inputIds, needleContentIds);

            return found ? [1f, 0f] : [0f, 1f];
        }

        private static bool ContainsSubsequence(long[] haystack, long[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}

using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

public class EmbeddingTests : TestFixture
{
    private readonly HashBasedEmbeddingGenerator _generator = new(8); // small dimension for tests
    private EmbeddingProjectionService _projectionService = null!;
    private ProjectionMatrixRepository _matrixRepo = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _matrixRepo = new ProjectionMatrixRepository(Factory);
        _projectionService = new EmbeddingProjectionService(_generator, _matrixRepo, new ArticleRepository(Factory), Session);
    }

    // ───── EmbeddingGenerator ─────────────────────────────────────────────────

    [Fact]
    public void EmbeddingGenerator_ProducesDeterministicEmbedding()
    {
        var gen = new HashBasedEmbeddingGenerator(384);
        var e1 = gen.Generate("same text");
        var e2 = gen.Generate("same text");
        e1.Should().BeEquivalentTo(e2);
    }

    [Fact]
    public void EmbeddingGenerator_DifferentTexts_ProduceDifferentEmbeddings()
    {
        var gen = new HashBasedEmbeddingGenerator(384);
        var e1 = gen.Generate("first text");
        var e2 = gen.Generate("completely different text");
        e1.Should().NotBeEquivalentTo(e2);
    }

    [Fact]
    public void EmbeddingGenerator_IsNormalized()
    {
        var gen = new HashBasedEmbeddingGenerator(384);
        var embedding = gen.Generate("normalization test");
        float norm = embedding.Sum(x => x * x);
        norm.Should().BeApproximately(1f, 0.001f);
    }

    // ───── ProjectionMatrix ───────────────────────────────────────────────────

    [Fact]
    public void ProjectionMatrix_IsOrthogonal()
    {
        // Use small dimension so test is fast
        int dim = 4;
        var pm = ProjectionMatrix.Generate(dim);

        // Verify P * P^T = I (M[i,j] = sum_k Q[i,k] * Q[j,k])
        // Apply basis vectors: Q * e_i = i-th column of Q
        // For an orthogonal matrix all rows are orthonormal
        float[] e = new float[dim];
        var projected = new float[dim][];
        for (int i = 0; i < dim; i++)
        {
            Array.Clear(e);
            e[i] = 1f;
            projected[i] = pm.Project(e);
        }

        // Verify that projections of unit vectors form an orthonormal basis
        for (int i = 0; i < dim; i++)
        {
            // ||Q * e_i|| = 1
            float norm = projected[i].Sum(x => x * x);
            norm.Should().BeApproximately(1f, 0.001f, $"row {i} should be a unit vector");

            for (int j = i + 1; j < dim; j++)
            {
                // Q * e_i · Q * e_j = 0
                float dot = 0f;
                for (int k = 0; k < dim; k++) dot += projected[i][k] * projected[j][k];
                dot.Should().BeApproximately(0f, 0.001f, $"rows {i} and {j} should be orthogonal");
            }
        }
    }

    [Fact]
    public void ProjectionMatrix_PreservesCosineDistance()
    {
        int dim = 8;
        var pm = ProjectionMatrix.Generate(dim);
        var gen = new HashBasedEmbeddingGenerator(dim);

        var e1 = gen.Generate("first vector");
        var e2 = gen.Generate("similar first vector");
        var e3 = gen.Generate("completely different text xyz 123");

        var p1 = pm.Project(e1);
        var p2 = pm.Project(e2);
        var p3 = pm.Project(e3);

        float cosOriginal12 = CosineSimilarity(e1, e2);
        float cosProjected12 = CosineSimilarity(p1, p2);
        float cosOriginal13 = CosineSimilarity(e1, e3);
        float cosProjected13 = CosineSimilarity(p1, p3);

        // Cosine similarity should be preserved after projection
        cosProjected12.Should().BeApproximately(cosOriginal12, 0.001f);
        cosProjected13.Should().BeApproximately(cosOriginal13, 0.001f);
    }

    [Fact]
    public void ProjectionMatrix_WrapUnwrap_Roundtrip()
    {
        int dim = 8;
        var pm = ProjectionMatrix.Generate(dim);

        var masterDek = new byte[32];
        Random.Shared.NextBytes(masterDek);

        var (encrypted, iv) = pm.Wrap(masterDek);
        var restored = ProjectionMatrix.Unwrap(encrypted, iv, masterDek);

        // Apply both to the same vector — results should match
        var gen = new HashBasedEmbeddingGenerator(dim);
        var vec = gen.Generate("test");
        var p1 = pm.Project(vec);
        var p2 = restored.Project(vec);

        for (int i = 0; i < dim; i++)
            p2[i].Should().BeApproximately(p1[i], 0.001f);
    }

    // ───── EmbeddingProjectionService ────────────────────────────────────────

    [Fact]
    public async Task EmbeddingProjectionService_EnsureMatrix_CreatesMatrix()
    {
        const string Password = "matrixPassword";
        await InitService.InitializeAsync("TestNode", Password);
        await Session.UnlockAsync(Password);

        await _projectionService.EnsureProjectionMatrixAsync();

        var stored = await _matrixRepo.GetAsync();
        stored.Should().NotBeNull();
        stored!.EncryptedMatrix.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EmbeddingProjectionService_ProjectArticle_SetsEmbedding()
    {
        const string Password = "projectionPassword";
        await InitService.InitializeAsync("TestNode", Password);
        await Session.UnlockAsync(Password);

        await _projectionService.EnsureProjectionMatrixAsync();

        var article = await ArticleService.CreateAsync("Article for projection", "/Test", [], "content for embedding");
        // embedding_pending = true initially
        article.EmbeddingPending.Should().BeTrue();

        await _projectionService.ProjectArticleAsync(article, "content for embedding");

        var updated = await new ArticleRepository(Factory).GetByIdAsync(article.Id);
        updated!.EmbeddingPending.Should().BeFalse();
        updated.EmbeddingProjection.Should().NotBeNull();
        updated.EmbeddingProjection!.Length.Should().Be(_generator.Dimension * 4);
    }

    [Fact]
    public async Task SemanticSearch_FindsSimilarArticles()
    {
        const string Password = "searchPassword";
        await InitService.InitializeAsync("TestNode", Password);
        await Session.UnlockAsync(Password);

        await _projectionService.EnsureProjectionMatrixAsync();

        var articleRepo = new ArticleRepository(Factory);

        // Use texts where one contains exactly the same words as the query
        // Hash-based embeddings: same words → same contribution → high similarity
        var a1 = await ArticleService.CreateAsync("Cat", "/Animals", [], "cat meows cat kitten");
        var a2 = await ArticleService.CreateAsync("Dog", "/Animals", [], "dog barks dog puppy");

        await _projectionService.ProjectArticleAsync(a1, "cat meows cat kitten");
        await _projectionService.ProjectArticleAsync(a2, "dog barks dog puppy");

        // Search by text "cat" — should find the cat article
        var queryProjection = await _projectionService.ProjectQueryAsync("cat");
        var results = await articleRepo.SearchByEmbeddingAsync(queryProjection, topK: 1);

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(a1.Id);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA * normB);
        return denom > 0 ? dot / denom : 0f;
    }
}

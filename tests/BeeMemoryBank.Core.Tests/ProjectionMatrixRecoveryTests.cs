using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Covers <see cref="EmbeddingProjectionService.EnsureProjectionMatrixAsync"/>'s repair path for a
/// projection matrix that can no longer be decrypted.
///
/// The matrix is sealed directly under the master DEK, and DEK rotation used to skip it entirely
/// (it has no per-row DEK columns, so the table-driven rewrap never saw it). A vault rotated by
/// such a build ends up with a matrix sealed under the retired DEK: every semantic query and every
/// background re-embed then throws CryptographicException, forever, with no way back. The rotation
/// side is fixed in DekRotationService.ReWrapProjectionMatrix, but vaults already in that state
/// need to heal themselves — that is what these tests pin down.
/// </summary>
public class ProjectionMatrixRecoveryTests : TestFixture
{
    private EmbeddingProjectionService _projectionService = null!;
    private ProjectionMatrixRepository _matrixRepo = null!;
    private ArticleRepository _articleRepo = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _matrixRepo = new ProjectionMatrixRepository(Factory);
        _articleRepo = new ArticleRepository(Factory, ScopeHolder);
        _projectionService = new EmbeddingProjectionService(
            new FakeEmbeddingGenerator(), _matrixRepo, _articleRepo, Session,
            ArticleChunker.CreateDefault(), new ArticleChunkEmbeddingRepository(Factory));

        await InitService.InitializeAsync("admin", "TestNode", MatrixPassword);
        await Session.UnlockAsync(MatrixPassword);
    }

    private const string MatrixPassword = "matrixRecoveryPwd1!";

    [Fact]
    public async Task AnUndecryptableMatrix_IsRegeneratedAndAllArticlesReFlaggedForEmbedding()
    {
        await _projectionService.EnsureProjectionMatrixAsync();
        var original = await _matrixRepo.GetAsync();
        original.Should().NotBeNull();

        var article = await ArticleService.CreateAsync("Indexed", "/Recovery", [], "some body text");
        // Simulate the article having already been embedded under the doomed matrix.
        await _articleRepo.UpdateEmbeddingUnscopedAsync(article.Id, new byte[16], "test-model");
        (await _articleRepo.GetByIdAsync(article.Id))!.EmbeddingPending.Should().BeFalse();

        // Reproduce the post-rotation state: same matrix bytes, sealed under a DEK nobody holds.
        var strandedDek = new byte[32];
        Random.Shared.NextBytes(strandedDek);
        var (stranded, strandedIv) = DekManager.WrapDek(new byte[4096], strandedDek);
        await _matrixRepo.SaveAsync(new ProjectionMatrixStore
        {
            EncryptedMatrix = stranded,
            IV = strandedIv,
            CreatedAt = DateTime.UtcNow
        });

        // Before the fix this returned early ("a row exists, nothing to do") and left the vault
        // permanently broken.
        await _projectionService.EnsureProjectionMatrixAsync();

        var repaired = await _matrixRepo.GetAsync();
        repaired.Should().NotBeNull();
        repaired!.EncryptedMatrix.Should().NotEqual(stranded, "the dead matrix must be replaced");

        // The replacement must be usable — this is the call that used to throw on every search.
        var projected = await _projectionService.ProjectQueryAsync("anything");
        projected.Should().NotBeNull();

        // Every stored projection was computed in the discarded matrix's space, so all of them
        // must be queued for recomputation rather than silently scored against the new one.
        (await _articleRepo.GetByIdAsync(article.Id))!
            .EmbeddingPending.Should().BeTrue("stale projections must be re-queued, not reused");
    }

    [Fact]
    public async Task AHealthyMatrix_IsLeftAloneAndDoesNotReFlagArticles()
    {
        await _projectionService.EnsureProjectionMatrixAsync();
        var original = await _matrixRepo.GetAsync();

        var article = await ArticleService.CreateAsync("Indexed", "/Recovery", [], "some body text");
        await _articleRepo.UpdateEmbeddingUnscopedAsync(article.Id, new byte[16], "test-model");

        // Idempotent no-op — the repair path must not fire on a matrix that opens fine, or every
        // background cycle would re-embed the whole vault.
        await _projectionService.EnsureProjectionMatrixAsync();

        var after = await _matrixRepo.GetAsync();
        after!.EncryptedMatrix.Should().Equal(original!.EncryptedMatrix);
        (await _articleRepo.GetByIdAsync(article.Id))!.EmbeddingPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that the model version EmbeddingProjectionService persists on every row it writes is
    /// the version reported by the wired <see cref="IEmbeddingGenerator"/>, not the hard-coded
    /// OnnxEmbeddingGenerator.Version constant. Anything else (a test fake, a future non-ONNX
    /// generator) would otherwise pin every row to the wrong version and silently look stale on
    /// every embedding cycle.
    /// </summary>
    [Fact]
    public async Task ProjectArticle_PersistsTheGeneratorVersionNotTheOnnxConstant()
    {
        const string customVersion = "alternate-embedding-v3";

        var customGenerator = new VersionedFakeEmbeddingGenerator(customVersion, dimension: 384);
        var customProjection = new EmbeddingProjectionService(
            customGenerator, _matrixRepo, _articleRepo, Session,
            ArticleChunker.CreateDefault(), new ArticleChunkEmbeddingRepository(Factory));

        await customProjection.EnsureProjectionMatrixAsync();

        var article = await ArticleService.CreateAsync(
            "Version Probe", "/version-probe", [], "body");

        await customProjection.ProjectArticleAsync(article, "body");

        // ArticleRepository.UpdateEmbeddingUnscopedAsync writes the supplied model_version string
        // verbatim into tbl_article.embedding_model_version, so the assertion reads it back through
        // the same path the search side reads it on.
        var row = await _articleRepo.GetByIdAsync(article.Id);
        row.Should().NotBeNull();
        row!.EmbeddingModelVersion.Should().Be(
            customVersion,
            "EmbeddingProjectionService must persist generator.Version, not the Onnx constant");
    }

    private sealed class VersionedFakeEmbeddingGenerator : IEmbeddingGenerator
    {
        private readonly int _dimension;
        private readonly string _version;

        public VersionedFakeEmbeddingGenerator(string version, int dimension)
        {
            _version = version;
            _dimension = dimension;
        }

        public int Dimension => _dimension;
        public string Version => _version;
        public float[] Generate(string text) => new float[_dimension];
        public float[] GenerateQuery(string text) => new float[_dimension];
    }

    [Fact]
    public async Task ALockedSession_DoesNotTouchAnExistingMatrix()
    {
        await _projectionService.EnsureProjectionMatrixAsync();
        var original = await _matrixRepo.GetAsync();

        Session.Lock();

        // Without the DEK there is nothing to verify and nothing to regenerate. Deleting or
        // rewriting the matrix here would destroy a perfectly good one on every locked cycle.
        await _projectionService.EnsureProjectionMatrixAsync();

        using var conn = Factory.CreateConnection();
        var rowCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_projection_matrix");
        rowCount.Should().Be(1);

        var after = await _matrixRepo.GetAsync();
        after!.EncryptedMatrix.Should().Equal(original!.EncryptedMatrix);
    }
}

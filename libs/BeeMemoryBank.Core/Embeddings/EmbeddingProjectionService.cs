using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Orchestrates embedding projection generation and storage for articles.
/// Requires an unlocked session (to decrypt the projection matrix).
/// </summary>
public class EmbeddingProjectionService(
    IEmbeddingGenerator generator,
    IProjectionMatrixRepository matrixRepo,
    IArticleRepository articleRepo,
    SessionService session,
    ArticleChunker chunker,
    IArticleChunkEmbeddingRepository chunkRepo)
{
    private const string ModelVersion = OnnxEmbeddingGenerator.Version;

    /// <summary>
    /// Initializes the projection matrix for the current node.
    /// Called once on first use of semantic search.
    /// </summary>
    public async Task EnsureProjectionMatrixAsync()
    {
        var stored = await matrixRepo.GetAsync();
        if (stored != null) return; // already initialized

        if (!session.IsUnlocked)
            throw new InvalidOperationException("Session is locked. Unlock to initialize the projection matrix.");

        var masterDek = session.GetMasterDek();
        try
        {
            var matrix = ProjectionMatrix.Generate(generator.Dimension);
            var (encryptedMatrix, iv) = matrix.Wrap(masterDek);

            await matrixRepo.SaveAsync(new ProjectionMatrixStore
            {
                EncryptedMatrix = encryptedMatrix,
                IV = iv,
                CreatedAt = DateTime.UtcNow
            });
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    /// <summary>
    /// Generates and saves an embedding projection for a single article: the full-document
    /// projection (unchanged, pre-WP-15 behavior — <see cref="ProjectQueryAsync"/> and any caller
    /// still relying on it keep working) plus, since WP-15, one projection per ~256-token chunk
    /// (<see cref="ArticleChunker"/>), so content past
    /// <see cref="OnnxEmbeddingGenerator.MaxSequenceLength"/> tokens — silently dropped by the
    /// single full-document embedding — is still searchable via its own chunk.
    /// </summary>
    public async Task ProjectArticleAsync(Article article, string plaintext)
    {
        if (!session.IsUnlocked)
            throw new InvalidOperationException("Session is locked.");

        // Never embed a protected article's body — it's an opaque passphrase-encrypted blob. Clear
        // the pending flag with an empty projection so the background processor stops retrying it;
        // it simply won't appear in semantic search (by design).
        if (Crypto.ProtectedContentCodec.IsProtected(plaintext))
        {
            await articleRepo.UpdateEmbeddingAsync(article.Id, [], ModelVersion);
            await chunkRepo.ReplaceChunksAsync(article.Id, [], ModelVersion);
            return;
        }

        var matrix = await LoadMatrixAsync();
        var embedding = generator.Generate(plaintext);
        var projection = matrix.Project(embedding);
        var projectionBytes = FloatsToBytes(projection);

        await articleRepo.UpdateEmbeddingAsync(article.Id, projectionBytes, ModelVersion);

        List<string> chunkTexts = chunker.Chunk(plaintext);
        var chunkRows = new List<(byte[] Projection, float Scale)>(chunkTexts.Count);
        for (int i = 0; i < chunkTexts.Count; i++)
        {
            // A single-chunk article's one chunk covers exactly the same token range the
            // full-document embedding above already computed (same token budget boundary) --
            // reuse that projection instead of re-running inference on effectively the same text.
            float[] chunkProjection = chunkTexts.Count == 1
                ? projection
                : matrix.Project(generator.Generate(chunkTexts[i]));
            var (quantized, scale, _) = Int8Quantizer.Quantize(chunkProjection);
            chunkRows.Add((quantized, scale));
        }
        await chunkRepo.ReplaceChunksAsync(article.Id, chunkRows, ModelVersion);
    }

    /// <summary>
    /// Projects a query for semantic search.
    /// </summary>
    public async Task<float[]> ProjectQueryAsync(string query)
    {
        var matrix = await LoadMatrixAsync();
        var embedding = generator.GenerateQuery(query);
        return matrix.Project(embedding);
    }

    private async Task<ProjectionMatrix> LoadMatrixAsync()
    {
        var stored = await matrixRepo.GetAsync()
            ?? throw new InvalidOperationException("Projection matrix not initialized. Call EnsureProjectionMatrixAsync.");
        var masterDek = session.GetMasterDek();
        try
        {
            return ProjectionMatrix.Unwrap(stored.EncryptedMatrix, stored.IV, masterDek);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}

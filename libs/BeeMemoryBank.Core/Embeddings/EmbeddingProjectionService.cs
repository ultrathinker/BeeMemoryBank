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
    SessionService session)
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
    /// Generates and saves an embedding projection for a single article.
    /// </summary>
    public async Task ProjectArticleAsync(Article article, string plaintext)
    {
        if (!session.IsUnlocked)
            throw new InvalidOperationException("Session is locked.");

        var matrix = await LoadMatrixAsync();
        var embedding = generator.Generate(plaintext);
        var projection = matrix.Project(embedding);
        var projectionBytes = FloatsToBytes(projection);

        await articleRepo.UpdateEmbeddingAsync(article.Id, projectionBytes, ModelVersion);
    }

    /// <summary>
    /// Projects a query for semantic search.
    /// </summary>
    public async Task<float[]> ProjectQueryAsync(string query)
    {
        var matrix = await LoadMatrixAsync();
        var embedding = generator.Generate(query);
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

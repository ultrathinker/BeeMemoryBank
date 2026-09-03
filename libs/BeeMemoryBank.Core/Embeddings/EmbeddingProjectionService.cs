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
    IArticleChunkEmbeddingRepository chunkRepo,
    MaintenanceModeService? maintenance = null)
{
    private const string ModelVersion = OnnxEmbeddingGenerator.Version;

    /// <summary>
    /// Initializes the projection matrix for the current node.
    /// Called once on first use of semantic search.
    /// </summary>
    public async Task EnsureProjectionMatrixAsync()
    {
        var stored = await matrixRepo.GetAsync();

        if (!session.IsUnlocked)
        {
            // Nothing to initialize and nothing to verify without the DEK. Keep the original
            // message for the genuinely-uninitialized case; a present-but-unverifiable matrix is
            // not an error here, the next unlocked pass will check it.
            if (stored != null) return;
            throw new InvalidOperationException("Session is locked. Unlock to initialize the projection matrix.");
        }

        if (stored != null && CanDecrypt(stored)) return; // already initialized and readable

        // A matrix that will not open is NOT proof it is dead while a heavy operation is in
        // flight. DEK rotation commits the re-wrapped matrix inside its transaction and only
        // swaps the in-memory master DEK AFTER the commit, so between those two points the row
        // is sealed under the NEW key while this service still holds the OLD one — and the
        // retired-DEK candidates cannot help, because the new key is not a candidate yet. The
        // background PendingEmbeddingProcessor calls this method every cycle, so without this
        // guard a rotation could land in that window and be met with a full regeneration:
        // every vector in the vault discarded, every article re-queued, and the freshly written
        // matrix overwritten with one sealed under the retired key — leaving the vault worse
        // than before and failing the same way on every later cycle. Rotation and restore both
        // hold maintenance mode across their whole operation, so backing off here costs one
        // cycle and nothing else.
        if (stored != null && maintenance?.IsInMaintenance == true) return;

        // Either no matrix yet, or one we can no longer decrypt. The latter is recoverable only by
        // regenerating: a matrix sealed under a key we don't have is not coming back, and every
        // projection derived from it is meaningless in the new matrix's space. This is the repair
        // path for vaults rotated by a build that did not re-wrap tbl_projection_matrix (see
        // DekRotationService.ReWrapProjectionMatrix) — without it, semantic search stayed broken
        // forever. A wrong-but-valid DEK cannot reach here: unlock verifies the DEK against the
        // node sentinel before caching it, so an unwrap failure means the matrix, not the key.
        bool regenerating = stored != null;

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

        if (regenerating)
        {
            // Re-flag everything for re-embedding. Chunk rows are replaced wholesale by
            // ProjectArticleAsync as each article is reprocessed, so they need no separate purge.
            await articleRepo.MarkAllEmbeddingsPendingAsync();
        }
    }

    /// <summary>
    /// True if the stored matrix unwraps under the current master DEK. A CryptographicException
    /// here is deterministic (AES-GCM tag mismatch), never transient, so treating it as
    /// "regenerate" cannot be triggered by a flaky read.
    /// </summary>
    private bool CanDecrypt(ProjectionMatrixStore stored)
    {
        try
        {
            // Retired DEKs included, the same way every article read does it: right after a
            // rotation the matrix a peer sent (or one written moments before the swap) is still
            // sealed under the previous key, and treating that as corruption would throw away
            // every vector in the vault for what is a perfectly recoverable row.
            session.TryUnwrapWithCandidates(dek =>
                ProjectionMatrix.Unwrap(stored.EncryptedMatrix, stored.IV, dek));
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
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
        // Candidates, not just the current DEK — must agree with CanDecrypt above, or a matrix
        // still sealed under a retired key would be judged healthy and then fail to load.
        return session.TryUnwrapWithCandidates(dek =>
            ProjectionMatrix.Unwrap(stored.EncryptedMatrix, stored.IV, dek));
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

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Secret orthogonal projection matrix for embeddings.
/// Preserves cosine similarity: cos(P*e1, P*e2) = cos(e1, e2) since P^T * P = I.
/// The matrix is stored encrypted with the master DEK.
/// </summary>
public class ProjectionMatrix
{
    private readonly float[] _matrix; // [dim x dim], row-major
    private readonly int _dim;

    private ProjectionMatrix(float[] matrix, int dim)
    {
        _matrix = matrix;
        _dim = dim;
    }

    /// <summary>Generates a new random orthogonal matrix using the Gram-Schmidt algorithm.</summary>
    public static ProjectionMatrix Generate(int dim = 384)
    {
        var Q = new float[dim * dim];

        for (int i = 0; i < dim; i++)
        {
            // Initialize with a cryptographically random vector
            float[] v = new float[dim];
            var buf = new byte[dim * 4];
            RandomNumberGenerator.Fill(buf);
            for (int k = 0; k < dim; k++)
                v[k] = (float)(BitConverter.ToInt32(buf, k * 4) / (double)int.MaxValue);

            // Subtract projections onto previous basis vectors
            for (int j = 0; j < i; j++)
            {
                float dot = 0f;
                for (int k = 0; k < dim; k++)
                    dot += v[k] * Q[j * dim + k];
                for (int k = 0; k < dim; k++)
                    v[k] -= dot * Q[j * dim + k];
            }

            // Normalize
            float norm = 0f;
            for (int k = 0; k < dim; k++) norm += v[k] * v[k];
            norm = MathF.Sqrt(norm);
            if (norm < 1e-10f)
            {
                // Degenerate case — set standard basis vector
                v = new float[dim];
                v[i] = 1f;
            }
            else
            {
                for (int k = 0; k < dim; k++) v[k] /= norm;
            }

            Array.Copy(v, 0, Q, i * dim, dim);
        }

        return new ProjectionMatrix(Q, dim);
    }

    /// <summary>Applies the projection matrix to a vector.</summary>
    public float[] Project(float[] embedding)
    {
        if (embedding.Length != _dim)
            throw new ArgumentException($"Expected vector of dimension {_dim}, got {embedding.Length}");

        var result = new float[_dim];
        for (int i = 0; i < _dim; i++)
        {
            float sum = 0f;
            for (int k = 0; k < _dim; k++)
                sum += _matrix[i * _dim + k] * embedding[k];
            result[i] = sum;
        }
        return result;
    }

    /// <summary>Serializes the matrix to bytes and encrypts with master DEK (AES-256-GCM).</summary>
    public (byte[] encryptedMatrix, byte[] iv) Wrap(byte[] masterDek)
    {
        var bytes = MemoryMarshal.AsBytes(_matrix.AsSpan()).ToArray();
        // WrapDek(data, key): first argument is data, second is key
        return DekManager.WrapDek(bytes, masterDek);
    }

    /// <summary>Decrypts and restores the matrix.</summary>
    public static ProjectionMatrix Unwrap(byte[] encryptedMatrix, byte[] iv, byte[] masterDek)
    {
        var bytes = DekManager.UnwrapDek(encryptedMatrix, iv, masterDek);
        if (bytes.Length % 4 != 0)
            throw new InvalidDataException("Corrupted projection matrix: length is not a multiple of 4.");

        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);

        var dim = (int)MathF.Round(MathF.Sqrt(floats.Length));
        if (dim * dim != floats.Length)
            throw new InvalidDataException($"Corrupted projection matrix: {floats.Length} floats don't form a square matrix.");

        return new ProjectionMatrix(floats, dim);
    }

    public int Dimension => _dim;
}

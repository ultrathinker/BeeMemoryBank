using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ProjectionMatrixRepository(DbConnectionFactory factory) : BaseRepository(factory), IProjectionMatrixRepository
{
    public async Task<ProjectionMatrixStore?> GetAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<ProjectionMatrixStore>(
            @"SELECT
                id               AS Id,
                encrypted_matrix AS EncryptedMatrix,
                iv               AS IV,
                created_at       AS CreatedAt
              FROM tbl_projection_matrix ORDER BY id DESC LIMIT 1");
    }

    public async Task SaveAsync(ProjectionMatrixStore matrix)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM tbl_projection_matrix", transaction: tx);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_projection_matrix (encrypted_matrix, iv, created_at)
              VALUES (@EncryptedMatrix, @IV, @CreatedAt)",
            new { matrix.EncryptedMatrix, matrix.IV, matrix.CreatedAt }, transaction: tx);
        tx.Commit();
    }
}

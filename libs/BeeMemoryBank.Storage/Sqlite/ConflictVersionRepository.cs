using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ConflictVersionRepository(DbConnectionFactory factory) : BaseRepository(factory), IConflictVersionRepository
{
    private const string SelectCols = @"
        id              AS Id,
        article_id      AS ArticleId,
        source_node_id  AS SourceNodeId,
        lamport_ts      AS LamportTs,
        ciphertext      AS Ciphertext,
        iv              AS IV,
        encrypted_dek   AS EncryptedDek,
        dek_iv          AS DekIV,
        created_at      AS CreatedAt,
        expires_at      AS ExpiresAt";

    public async Task CreateAsync(ConflictVersion conflict)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_conflict_version
              (id, article_id, source_node_id, lamport_ts, ciphertext, iv, encrypted_dek, dek_iv, metadata_json, created_at, expires_at)
              VALUES (@Id, @ArticleId, @SourceNodeId, @LamportTs, @Ciphertext, @IV, @EncryptedDek, @DekIV, @MetadataJson, @CreatedAt, @ExpiresAt)",
            conflict);
    }

    public async Task<List<ConflictVersion>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<ConflictVersion>(
            $"SELECT {SelectCols} FROM tbl_conflict_version WHERE article_id = @articleId ORDER BY lamport_ts DESC",
            new { articleId })).ToList();
    }

    public async Task<int> DeleteExpiredAsync(DateTime now)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM tbl_conflict_version WHERE expires_at < @now",
            new { now });
    }
}

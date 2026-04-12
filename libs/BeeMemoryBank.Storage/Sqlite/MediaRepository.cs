using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class MediaRepository(DbConnectionFactory factory) : BaseRepository(factory), IMediaRepository
{
    private const string SelectCols = @"
        m.id              AS Id,
        m.article_id      AS ArticleId,
        m.file_name       AS FileName,
        m.content_type    AS ContentType,
        m.file_size       AS FileSize,
        m.encrypted_dek   AS EncryptedDek,
        m.dek_iv          AS DekIV,
        m.iv              AS IV,
        m.status          AS Status,
        m.lamport_ts      AS LamportTs,
        m.source_node_id  AS SourceNodeId,
        m.created_at      AS CreatedAt,
        m.deleted_at      AS DeletedAt";

    public async Task<Media?> GetByIdAsync(Guid id, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        var sql = includeDeleted
            ? $"SELECT {SelectCols} FROM tbl_media m WHERE m.id = @id"
            : $"SELECT {SelectCols} FROM tbl_media m WHERE m.id = @id AND m.status = 'A'";
        return await conn.QuerySingleOrDefaultAsync<Media>(sql, new { id });
    }

    public async Task<List<Media>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Media>(
            $"SELECT {SelectCols} FROM tbl_media m WHERE m.article_id = @articleId AND m.status = 'A'",
            new { articleId })).ToList();
    }

    public async Task CreateAsync(Media media)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_media
              (id, article_id, file_name, content_type, file_size,
               encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at)
              VALUES (@Id, @ArticleId, @FileName, @ContentType, @FileSize,
                      @EncryptedDek, @DekIV, @IV, @Status, @LamportTs, @SourceNodeId, @CreatedAt)",
            media);
    }

    public async Task SoftDeleteByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_media SET status = 'D', deleted_at = @now WHERE article_id = @articleId AND status = 'A'",
            new { articleId, now });
    }

    public async Task<List<Media>> GetDeletedOlderThanAsync(DateTime cutoff)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Media>(
            $"SELECT {SelectCols} FROM tbl_media m WHERE m.status = 'D' AND m.deleted_at < @cutoff",
            new { cutoff })).ToList();
    }

    public async Task<List<Media>> GetOrphanedOlderThanAsync(DateTime cutoff)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Media>(
            $@"SELECT {SelectCols} FROM tbl_media m
               WHERE m.status = 'A' AND m.article_id IS NULL AND m.created_at < @cutoff",
            new { cutoff })).ToList();
    }

    public async Task DeleteByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_media WHERE id = @id", new { id });
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_media SET status = 'D', deleted_at = @now WHERE id = @id AND status = 'A'",
            new { id, now });
    }
}

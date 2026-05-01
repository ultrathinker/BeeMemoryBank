using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class MediaRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder) : BaseRepository(factory), IMediaRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;
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
        var media = await conn.QuerySingleOrDefaultAsync<Media>(sql, new { id });

        if (media == null) return null;

        if (!_holder.Scope.IsSuperadmin)
        {
            if (!media.ArticleId.HasValue)
                return null;
            var treePath = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
                new { articleId = media.ArticleId.Value });
            if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
                return null;
        }

        return media;
    }

    public async Task<List<Media>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();

        if (!_holder.Scope.IsSuperadmin)
        {
            var treePath = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
                new { articleId });
            if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
                return [];
        }

        return (await conn.QueryAsync<Media>(
            $"SELECT {SelectCols} FROM tbl_media m WHERE m.article_id = @articleId AND m.status = 'A'",
            new { articleId })).ToList();
    }

    private async Task EnsureWriteAllowedAsync(System.Data.IDbConnection conn, Guid? articleId)
    {
        if (_holder.Scope.IsSuperadmin) return;
        if (!articleId.HasValue) return;  // orphaned upload, scope check happens via article creation
        var treePath = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
            new { articleId = articleId.Value });
        if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
            throw new UnauthorizedAccessException($"Write access denied for media on article {articleId}");
    }

    public async Task CreateAsync(Media media)
    {
        using var conn = OpenConnection();
        await EnsureWriteAllowedAsync(conn, media.ArticleId);
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
        await EnsureWriteAllowedAsync(conn, articleId);
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
        if (!_holder.Scope.IsSuperadmin)
        {
            var articleId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT article_id FROM tbl_media WHERE id = @id", new { id });
            if (articleId != null && Guid.TryParse(articleId, out var aid))
                await EnsureWriteAllowedAsync(conn, aid);
        }
        var now = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_media SET status = 'D', deleted_at = @now WHERE id = @id AND status = 'A'",
            new { id, now });
    }

    public async Task UpdateLamportTsAsync(Guid id, long lamportTs, Guid? sourceNodeId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_media SET lamport_ts = @lamportTs, source_node_id = @sourceNodeId WHERE id = @id",
            new { id, lamportTs, sourceNodeId });
    }

    public async Task<List<Guid>> LinkOrphansToArticleAsync(IEnumerable<Guid> mediaIds, Guid articleId, long lamportTs, Guid? sourceNodeId)
    {
        using var conn = OpenConnection();
        var ids = mediaIds.ToList();
        var linked = await conn.QueryAsync<string>(
            @"UPDATE tbl_media
              SET article_id = @articleId, lamport_ts = @lamportTs, source_node_id = @sourceNodeId
              WHERE id IN @ids AND article_id IS NULL AND status = 'A'
              RETURNING id",
            new { ids, articleId, lamportTs, sourceNodeId });
        return linked.Select(Guid.Parse).ToList();
    }
}

using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class CommentRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder) : BaseRepository(factory), ICommentRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;

    private const string SelectColumns =
        @"id AS Id, comment_id AS CommentId, article_id AS ArticleId,
          text AS Text, source_node_id AS SourceNodeId, created_at AS CreatedAt,
          lamport_ts AS LamportTs,
          ciphertext AS Ciphertext, iv AS IV, encrypted AS Encrypted,
          deleted_at AS DeletedAt, delete_lamport_ts AS DeleteLamportTs,
          delete_node_id AS DeleteNodeId";

    // All Guid bindings in this repository pass through Dapper's GuidTypeHandler so
    // Microsoft.Data.Sqlite normalizes to uppercase TEXT — matching tbl_article.id and
    // tbl_folder.id. tbl_comment's legacy lowercase rows are uppercased by migration
    // 005_normalize_comment_guid_case.sql.

    private async Task<string?> GetArticleTreePathAsync(IDbConnection conn, Guid articleId)
    {
        return await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
            new { articleId });
    }

    public async Task<Comment?> GetByIdAsync(int id)
    {
        using var conn = OpenConnection();
        var comment = await conn.QuerySingleOrDefaultAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE id = @id AND deleted_at IS NULL", new { id });

        if (comment == null) return null;

        if (!_holder.Scope.IsSuperadmin)
        {
            var treePath = await GetArticleTreePathAsync(conn, comment.ArticleId);
            if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
                return null;
        }

        return comment;
    }

    public async Task<List<Comment>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();

        if (!_holder.Scope.IsSuperadmin)
        {
            var treePath = await GetArticleTreePathAsync(conn, articleId);
            if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
                return [];
        }

        return (await conn.QueryAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE article_id = @articleId AND deleted_at IS NULL ORDER BY created_at ASC",
            new { articleId })).ToList();
    }

    private async Task EnsureWriteAllowedAsync(IDbConnection conn, Guid articleId)
    {
        if (_holder.Scope.IsSuperadmin) return;
        var treePath = await GetArticleTreePathAsync(conn, articleId);
        if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
            throw new UnauthorizedAccessException($"Write access denied for article {articleId}");
    }

    public async Task<Comment> CreateAsync(Guid articleId, string text, Guid? sourceNodeId = null)
    {
        using var conn = OpenConnection();
        await EnsureWriteAllowedAsync(conn, articleId);
        var now = UtcNow();
        var commentId = Guid.NewGuid();
        var id = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, source_node_id, created_at, encrypted)
              VALUES (@commentId, @articleId, @text, @sourceNodeId, @now, 0);
              SELECT last_insert_rowid()",
            new { commentId, articleId, text, sourceNodeId, now });
        return (await conn.QuerySingleAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE id = @id", new { id }));
    }

    public async Task<Comment> CreateEncryptedAsync(Guid articleId, Guid commentId, byte[] ciphertext, byte[] iv, Guid? sourceNodeId = null)
    {
        using var conn = OpenConnection();
        await EnsureWriteAllowedAsync(conn, articleId);
        var now = UtcNow();
        var id = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, source_node_id, created_at, ciphertext, iv, encrypted)
              VALUES (@commentId, @articleId, '', @sourceNodeId, @now, @ciphertext, @iv, 1);
              SELECT last_insert_rowid()",
            new { commentId, articleId, sourceNodeId, now, ciphertext, iv });
        return (await conn.QuerySingleAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE id = @id", new { id }));
    }

    public async Task<Comment?> GetByCommentIdAsync(Guid commentId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE comment_id = @commentId",
            new { commentId });
    }

    public async Task CreateFromSyncAsync(Comment comment)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, source_node_id, created_at, ciphertext, iv, encrypted, lamport_ts)
              VALUES (@CommentId, @ArticleId, @Text, @SourceNodeId, @CreatedAt, @Ciphertext, @IV, @Encrypted, @LamportTs)",
            new { comment.CommentId, comment.ArticleId, comment.Text, comment.SourceNodeId,
                  comment.CreatedAt, comment.Ciphertext, comment.IV,
                  Encrypted = comment.Encrypted ? 1 : 0, comment.LamportTs });
    }

    public async Task UpdateLamportTsAsync(Guid commentId, long lamportTs, Guid? sourceNodeId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_comment SET lamport_ts = @lamportTs, source_node_id = @sourceNodeId WHERE comment_id = @commentId",
            new { commentId, lamportTs, sourceNodeId });
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = OpenConnection();
        if (!_holder.Scope.IsSuperadmin)
        {
            var articleId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT article_id FROM tbl_comment WHERE id = @id", new { id });
            if (articleId != null && Guid.TryParse(articleId, out var aid))
                await EnsureWriteAllowedAsync(conn, aid);
        }
        await conn.ExecuteAsync("UPDATE tbl_comment SET deleted_at = @now WHERE id = @id",
            new { id, now = UtcNow() });
    }

    public async Task SoftDeleteAsync(Guid commentId, long lamportTs, Guid? sourceNodeId)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        await conn.ExecuteAsync(
            @"UPDATE tbl_comment
              SET deleted_at = @now, delete_lamport_ts = @lamportTs, delete_node_id = @sourceNodeId
              WHERE comment_id = @commentId",
            new { commentId, now, lamportTs, sourceNodeId });
    }

    public async Task SoftDeletePlaceholderAsync(Guid commentId, long lamportTs, Guid? sourceNodeId)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_comment
                (comment_id, article_id, text, source_node_id, created_at, lamport_ts, encrypted,
                 deleted_at, delete_lamport_ts, delete_node_id)
              VALUES
                (@commentId, @emptyGuid, '', @sourceNodeId, @now, 0, 0,
                 @deletedAt, @lamportTs, @sourceNodeId)",
            new { commentId, emptyGuid = Guid.Empty, sourceNodeId,
                  now, deletedAt = now, lamportTs });
    }

    public async Task ResurrectFromSyncAsync(Guid commentId, Comment data)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_comment
              SET article_id        = @ArticleId,
                  text              = @Text,
                  source_node_id    = @SourceNodeId,
                  created_at        = @CreatedAt,
                  lamport_ts        = @LamportTs,
                  ciphertext        = @Ciphertext,
                  iv                = @IV,
                  encrypted         = @Encrypted,
                  deleted_at        = NULL,
                  delete_lamport_ts = NULL,
                  delete_node_id    = NULL
              WHERE comment_id = @CommentId",
            new { CommentId = commentId, data.ArticleId, data.Text, data.SourceNodeId,
                  data.CreatedAt, data.LamportTs, data.Ciphertext, data.IV,
                  Encrypted = data.Encrypted ? 1 : 0 });
    }

    public async Task<int> PurgeSoftDeletedOlderThanAsync(DateTime cutoff)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM tbl_comment WHERE deleted_at IS NOT NULL AND deleted_at < @cutoff",
            new { cutoff = cutoff.ToString("o") });
    }
}

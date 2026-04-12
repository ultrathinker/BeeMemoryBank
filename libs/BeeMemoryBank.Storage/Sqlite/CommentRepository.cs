using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class CommentRepository(DbConnectionFactory factory) : BaseRepository(factory), ICommentRepository
{
    private const string SelectColumns =
        @"id AS Id, comment_id AS CommentId, article_id AS ArticleId,
          text AS Text, source_node_id AS SourceNodeId, created_at AS CreatedAt,
          ciphertext AS Ciphertext, iv AS IV, encrypted AS Encrypted";

    public async Task<Comment?> GetByIdAsync(int id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE id = @id", new { id });
    }

    public async Task<List<Comment>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE article_id = @articleId ORDER BY created_at ASC",
            new { articleId = articleId.ToString() })).ToList();
    }

    public async Task<Comment> CreateAsync(Guid articleId, string text, Guid? sourceNodeId = null)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        var commentId = Guid.NewGuid().ToString();
        var id = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, source_node_id, created_at, encrypted)
              VALUES (@commentId, @articleId, @text, @sourceNodeId, @now, 0);
              SELECT last_insert_rowid()",
            new { commentId, articleId = articleId.ToString(), text,
                  sourceNodeId = sourceNodeId?.ToString(), now });
        return (await conn.QuerySingleAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE id = @id", new { id }));
    }

    public async Task<Comment> CreateEncryptedAsync(Guid articleId, byte[] ciphertext, byte[] iv, Guid? sourceNodeId = null)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        var commentId = Guid.NewGuid().ToString();
        var id = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, source_node_id, created_at, ciphertext, iv, encrypted)
              VALUES (@commentId, @articleId, '', @sourceNodeId, @now, @ciphertext, @iv, 1);
              SELECT last_insert_rowid()",
            new { commentId, articleId = articleId.ToString(),
                  sourceNodeId = sourceNodeId?.ToString(), now, ciphertext, iv });
        return (await conn.QuerySingleAsync<Comment>(
            $"SELECT {SelectColumns} FROM tbl_comment WHERE id = @id", new { id }));
    }

    public async Task<bool> ExistsByCommentIdAsync(Guid commentId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM tbl_comment WHERE comment_id = @commentId",
            new { commentId = commentId.ToString() }) > 0;
    }

    public async Task CreateFromSyncAsync(Comment comment)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, source_node_id, created_at, ciphertext, iv, encrypted)
              VALUES (@CommentId, @ArticleId, @Text, @SourceNodeId, @CreatedAt, @Ciphertext, @IV, @Encrypted)",
            new { CommentId = comment.CommentId.ToString(), ArticleId = comment.ArticleId.ToString(),
                  comment.Text, SourceNodeId = comment.SourceNodeId?.ToString(), comment.CreatedAt,
                  comment.Ciphertext, comment.IV, Encrypted = comment.Encrypted ? 1 : 0 });
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_comment WHERE id = @id", new { id });
    }

    public async Task DeleteByCommentIdAsync(Guid commentId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_comment WHERE comment_id = @commentId",
            new { commentId = commentId.ToString() });
    }
}

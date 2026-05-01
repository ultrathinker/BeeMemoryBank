using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ArticleVersionRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder) : BaseRepository(factory), IArticleVersionRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;

    private async Task<bool> IsArticleAccessibleAsync(IDbConnection conn, Guid articleId)
    {
        if (_holder.Scope.IsSuperadmin) return true;
        var treePath = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
            new { articleId });
        return treePath != null && !_holder.Scope.IsAccessDenied(treePath);
    }
    public async Task<List<ArticleVersion>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();

        if (!await IsArticleAccessibleAsync(conn, articleId))
            return [];

        var rows = await conn.QueryAsync(
            @"SELECT id AS Id, article_id AS ArticleId, version_number AS VersionNumber,
                     title AS Title, tree_path AS TreePath,
                     updated_by AS UpdatedBy, created_at AS CreatedAt
              FROM tbl_article_version
              WHERE article_id = @articleId
              ORDER BY version_number DESC",
            new { articleId });

        return rows.Select(r => new ArticleVersion
        {
            Id = Guid.Parse((string)r.Id),
            ArticleId = Guid.Parse((string)r.ArticleId),
            VersionNumber = (int)(long)r.VersionNumber,
            Title = (string)r.Title,
            TreePath = (string)r.TreePath,
            UpdatedBy = (string?)r.UpdatedBy,
            CreatedAt = DateTime.Parse((string)r.CreatedAt)
        }).ToList();
    }

    public async Task<ArticleVersion?> GetAsync(Guid articleId, int versionNumber)
    {
        using var conn = OpenConnection();

        if (!await IsArticleAccessibleAsync(conn, articleId))
            return null;

        var row = await conn.QuerySingleOrDefaultAsync(
            @"SELECT id AS Id, article_id AS ArticleId, version_number AS VersionNumber,
                     title AS Title, tree_path AS TreePath,
                     ciphertext AS Ciphertext, iv AS IV, encrypted_dek AS EncryptedDek, dek_iv AS DekIV,
                     updated_by AS UpdatedBy, created_at AS CreatedAt
              FROM tbl_article_version
              WHERE article_id = @articleId AND version_number = @versionNumber",
            new { articleId, versionNumber });

        if (row == null) return null;

        return new ArticleVersion
        {
            Id = Guid.Parse((string)row.Id),
            ArticleId = Guid.Parse((string)row.ArticleId),
            VersionNumber = (int)(long)row.VersionNumber,
            Title = (string)row.Title,
            TreePath = (string)row.TreePath,
            Ciphertext = (byte[])row.Ciphertext,
            IV = (byte[])row.IV,
            EncryptedDek = (byte[])row.EncryptedDek,
            DekIV = (byte[])row.DekIV,
            UpdatedBy = (string?)row.UpdatedBy,
            CreatedAt = DateTime.Parse((string)row.CreatedAt)
        };
    }

    public async Task<int> GetMaxVersionNumberAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(version_number), 0) FROM tbl_article_version WHERE article_id = @articleId",
            new { articleId });
    }

    public async Task CreateAsync(ArticleVersion version)
    {
        using var conn = OpenConnection();
        if (!await IsArticleAccessibleAsync(conn, version.ArticleId))
            throw new UnauthorizedAccessException($"Write access denied for version on article {version.ArticleId}");
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article_version
              (id, article_id, version_number, title, tree_path, ciphertext, iv, encrypted_dek, dek_iv, updated_by, created_at)
              VALUES (@Id, @ArticleId, @VersionNumber, @Title, @TreePath, @Ciphertext, @IV, @EncryptedDek, @DekIV, @UpdatedBy, @CreatedAt)",
            new
            {
                version.Id,
                version.ArticleId,
                version.VersionNumber,
                version.Title,
                version.TreePath,
                version.Ciphertext,
                version.IV,
                version.EncryptedDek,
                version.DekIV,
                version.UpdatedBy,
                version.CreatedAt
            });
    }

    public async Task DeleteOldVersionsAsync(Guid articleId, int keepCount)
    {
        using var conn = OpenConnection();
        if (!await IsArticleAccessibleAsync(conn, articleId))
            throw new UnauthorizedAccessException($"Write access denied for versions on article {articleId}");
        await conn.ExecuteAsync(
            @"DELETE FROM tbl_article_version
              WHERE article_id = @articleId
              AND version_number NOT IN (
                  SELECT version_number FROM tbl_article_version
                  WHERE article_id = @articleId
                  ORDER BY version_number DESC
                  LIMIT @keepCount
              )",
            new { articleId, keepCount });
    }

}

using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ArticleVersionRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder) : BaseRepository(factory), IArticleVersionRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;

    private async Task<bool> IsArticleAccessibleAsync(IDbConnection conn, Guid articleId, IDbTransaction? transaction = null)
    {
        if (_holder.Scope.IsSuperadmin) return true;
        var treePath = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
            new { articleId }, transaction: transaction);
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
            @"SELECT v.id AS Id, v.article_id AS ArticleId, v.version_number AS VersionNumber,
                     v.title AS Title, v.tree_path AS TreePath,
                     bl.data AS Ciphertext,
                     v.iv AS IV, v.encrypted_dek AS EncryptedDek, v.dek_iv AS DekIV,
                     v.updated_by AS UpdatedBy, v.created_at AS CreatedAt
              FROM tbl_article_version v
              LEFT JOIN tbl_blob bl ON bl.hash = v.ciphertext_hash
              WHERE v.article_id = @articleId AND v.version_number = @versionNumber",
            new { articleId, versionNumber });

        if (row == null) return null;

        return new ArticleVersion
        {
            Id = Guid.Parse((string)row.Id),
            ArticleId = Guid.Parse((string)row.ArticleId),
            VersionNumber = (int)(long)row.VersionNumber,
            Title = (string)row.Title,
            TreePath = (string)row.TreePath,
            Ciphertext = (byte[]?)row.Ciphertext
                ?? throw new InvalidOperationException($"Version {row.Id} of article {articleId} has no ciphertext blob in tbl_blob."),
            IV = (byte[])row.IV,
            EncryptedDek = (byte[])row.EncryptedDek,
            DekIV = (byte[])row.DekIV,
            UpdatedBy = (string?)row.UpdatedBy,
            CreatedAt = DateTime.Parse((string)row.CreatedAt)
        };
    }

    public async Task<ArticleVersion?> GetEarliestAfterAsync(Guid articleId, DateTime baselineAt)
    {
        using var conn = OpenConnection();

        if (!await IsArticleAccessibleAsync(conn, articleId))
            return null;

        var row = await conn.QuerySingleOrDefaultAsync(
            @"SELECT v.id AS Id, v.article_id AS ArticleId, v.version_number AS VersionNumber,
                     v.title AS Title, v.tree_path AS TreePath,
                     bl.data AS Ciphertext,
                     v.iv AS IV, v.encrypted_dek AS EncryptedDek, v.dek_iv AS DekIV,
                     v.updated_by AS UpdatedBy, v.created_at AS CreatedAt
              FROM tbl_article_version v
              LEFT JOIN tbl_blob bl ON bl.hash = v.ciphertext_hash
              WHERE v.article_id = @articleId AND v.created_at > @baselineAt
              ORDER BY v.created_at ASC LIMIT 1",
            new { articleId, baselineAt });

        if (row == null) return null;

        return new ArticleVersion
        {
            Id = Guid.Parse((string)row.Id),
            ArticleId = Guid.Parse((string)row.ArticleId),
            VersionNumber = (int)(long)row.VersionNumber,
            Title = (string)row.Title,
            TreePath = (string)row.TreePath,
            Ciphertext = (byte[]?)row.Ciphertext
                ?? throw new InvalidOperationException($"Version {row.Id} of article {articleId} has no ciphertext blob in tbl_blob."),
            IV = (byte[])row.IV,
            EncryptedDek = (byte[])row.EncryptedDek,
            DekIV = (byte[])row.DekIV,
            UpdatedBy = (string?)row.UpdatedBy,
            CreatedAt = DateTime.Parse((string)row.CreatedAt)
        };
    }

    public async Task<int> GetMaxVersionNumberAsync(Guid articleId, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(version_number), 0) FROM tbl_article_version WHERE article_id = @articleId",
                new { articleId }, transaction);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

    public async Task CreateAsync(ArticleVersion version, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            if (!await IsArticleAccessibleAsync(conn, version.ArticleId, transaction))
                throw new UnauthorizedAccessException($"Write access denied for version on article {version.ArticleId}");
            // Blob first, same transaction — see ArticleBodyRepository.UpsertAsync for why the
            // order is what keeps the collector from sweeping a blob that is about to be used.
            var hash = BlobHash.Compute(version.Ciphertext);
            await conn.ExecuteAsync(
                @"INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at)
                  VALUES (@hash, @data, @size, @createdAt)",
                new { hash, data = version.Ciphertext, size = version.Ciphertext.LongLength,
                      createdAt = DateTime.UtcNow.ToString("o") }, transaction);

            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article_version
                  (id, article_id, version_number, title, tree_path, ciphertext_hash, iv, encrypted_dek, dek_iv, updated_by, created_at)
                  VALUES (@Id, @ArticleId, @VersionNumber, @Title, @TreePath, @CiphertextHash, @IV, @EncryptedDek, @DekIV, @UpdatedBy, @CreatedAt)",
                new
                {
                    CiphertextHash = hash,
                    version.Id,
                    version.ArticleId,
                    version.VersionNumber,
                    version.Title,
                    version.TreePath,
                    version.IV,
                    version.EncryptedDek,
                    version.DekIV,
                    version.UpdatedBy,
                    version.CreatedAt
                }, transaction);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

    public async Task DeleteOldVersionsAsync(Guid articleId, int keepCount, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            if (!await IsArticleAccessibleAsync(conn, articleId, transaction))
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
                new { articleId, keepCount }, transaction);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

}

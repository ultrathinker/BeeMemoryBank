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
        m.deleted_at      AS DeletedAt,
        m.kind            AS Kind,
        m.uploaded_by     AS UploadedBy,
        m.ciphertext_sha256 AS CiphertextSha256";

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

    private async Task EnsureWriteAllowedAsync(System.Data.IDbConnection conn, Guid? articleId, System.Data.IDbTransaction? transaction = null)
    {
        if (_holder.Scope.IsSuperadmin) return;
        if (!articleId.HasValue)
        {
            // Unlinked upload (no article owns it yet): there is no folder ACL to evaluate, so the
            // scope check has nothing to check. Fail closed when there is no identity at all
            // (MediaOwnerKey == null) — that is what an anonymous caller resolves to, and letting
            // such a caller create a row that replicates to every peer is the hole B2 closes at
            // the repository layer. A uploader-present caller (Web proxy, MCP agent, chat tool
            // dispatcher, Obsidian/Bee import) keeps the previous behaviour: scope check happens
            // when the row is later linked or read.
            if (_holder.Scope.MediaOwnerKey == null)
                throw new UnauthorizedAccessException(
                    "Unlinked media upload requires an authenticated caller.");
            return;
        }
        var treePath = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @articleId AND a.status = 'A'",
            new { articleId = articleId.Value }, transaction: transaction);
        if (treePath == null || _holder.Scope.IsAccessDenied(treePath))
            throw new UnauthorizedAccessException($"Write access denied for media on article {articleId}");
        if (_holder.Scope.IsReadOnly(treePath))
            throw new ReadOnlyAccessException(treePath);
    }

    public async Task CreateAsync(Media media, System.Data.IDbTransaction? transaction = null)
    {
        // An unlinked upload remembers who made it (from the ambient caller scope unless the caller
        // set it), so only that uploader can later link, read or delete it — see the link methods.
        if (media.ArticleId == null)
            media.UploadedBy ??= _holder.Scope.MediaOwnerKey;
        else
            media.UploadedBy = null;

        const string insertSql = @"INSERT INTO tbl_media
              (id, article_id, file_name, content_type, file_size,
               encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at, kind,
               ciphertext_sha256, uploaded_by)
              VALUES (@Id, @ArticleId, @FileName, @ContentType, @FileSize,
                      @EncryptedDek, @DekIV, @IV, @Status, @LamportTs, @SourceNodeId, @CreatedAt, @Kind,
                      @CiphertextSha256, @UploadedBy)";

        if (transaction != null)
        {
            // Caller-supplied transaction: guard and write already share the caller's
            // connection, so just run them against it in order -- unchanged from before
            // this fix.
            await EnsureWriteAllowedAsync(transaction.Connection!, media.ArticleId, transaction);
            await transaction.Connection!.ExecuteAsync(insertSql, media, transaction);
        }
        else
        {
            // SECURITY: EnsureWriteAllowedAsync's guard reads the OWNING ARTICLE's current
            // tree path via a SELECT separate from the INSERT below. This used to run both
            // on the SAME connection but with NO shared transaction (autocommit) -- each
            // statement takes and releases SQLite's lock on its own, so a concurrent
            // ArticleRepository.UpdateAsync could still move the article into a denied
            // folder in the gap between the guard's read and this INSERT, and the media
            // row would be created under an authorization decision that was already stale.
            // BeginTransaction() issues BEGIN IMMEDIATE, which takes the write lock the
            // instant the transaction opens (see DbConnectionFactory.CreateConnection), so
            // running the guard AND the insert inside the SAME transaction closes that
            // window: a concurrent mover blocks on the write lock until this transaction
            // commits or rolls back. Do not go back to checking and writing as two separate
            // autocommit statements "to simplify" -- that's the bug this fixes. Keep
            // everything between BeginTransaction() and Commit() cheap.
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            await EnsureWriteAllowedAsync(conn, media.ArticleId, tx);
            await conn.ExecuteAsync(insertSql, media, tx);

            tx.Commit();
        }
    }

    public async Task SoftDeleteByArticleIdAsync(Guid articleId, System.Data.IDbTransaction? transaction = null)
    {
        if (transaction != null)
        {
            // Caller-supplied transaction: guard and write already share the caller's
            // connection, so just run them against it in order -- unchanged from before
            // this fix.
            await EnsureWriteAllowedAsync(transaction.Connection!, articleId, transaction);
            var now = UtcNow();
            await transaction.Connection!.ExecuteAsync(
                "UPDATE tbl_media SET status = 'D', deleted_at = @now WHERE article_id = @articleId AND status = 'A'",
                new { articleId, now }, transaction);
        }
        else
        {
            // SECURITY: same reasoning as CreateAsync above -- the guard's article-tree-path
            // read and this UPDATE must run inside the SAME transaction (not merely the same
            // connection) so a concurrent move of the owning article can't slip into the gap
            // between them.
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            await EnsureWriteAllowedAsync(conn, articleId, tx);
            var now = UtcNow();
            await conn.ExecuteAsync(
                "UPDATE tbl_media SET status = 'D', deleted_at = @now WHERE article_id = @articleId AND status = 'A'",
                new { articleId, now }, tx);

            tx.Commit();
        }
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
        // SECURITY: same TOCTOU shape as CreateAsync/SoftDeleteByArticleIdAsync above -- the
        // owning-article lookup, the tree-path guard, and the UPDATE all used to share a
        // connection but no transaction (autocommit), so a concurrent move of the owning
        // article could land between the guard's read and this UPDATE. No IDbTransaction
        // parameter is exposed on this method (nothing currently needs to compose it with
        // another write), so it is fully self-contained: open one transaction, run the guard
        // and the write against it, commit.
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        if (!_holder.Scope.IsSuperadmin)
        {
            var articleId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT article_id FROM tbl_media WHERE id = @id", new { id }, transaction: tx);
            if (articleId != null && Guid.TryParse(articleId, out var aid))
                await EnsureWriteAllowedAsync(conn, aid, tx);
        }
        var now = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_media SET status = 'D', deleted_at = @now WHERE id = @id AND status = 'A'",
            new { id, now }, tx);

        tx.Commit();
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
        // Body-referenced media (![..](/api/media/{id})): a non-superadmin may only link rows THEY
        // uploaded, otherwise an id learned from someone else, written into a body, would pull that
        // person's unlinked image into an article the caller can read. Superadmin and system work
        // (sync replay, import running as system) are unrestricted. A non-superadmin without an
        // owner key links nothing (uploaded_by = NULL never matches).
        var unrestricted = _holder.Scope.IsSuperadmin;
        var owner = _holder.Scope.MediaOwnerKey;
        using var conn = OpenConnection();
        var ids = mediaIds.ToList();
        var linked = await conn.QueryAsync<string>(
            @"UPDATE tbl_media
              SET article_id = @articleId, lamport_ts = @lamportTs, source_node_id = @sourceNodeId,
                  uploaded_by = NULL
              WHERE id IN @ids AND article_id IS NULL AND status = 'A'
                AND (@unrestricted = 1 OR uploaded_by = @owner)
              RETURNING id",
            new { ids, articleId, lamportTs, sourceNodeId, unrestricted = unrestricted ? 1 : 0, owner });
        return linked.Select(Guid.Parse).ToList();
    }

    public async Task<List<Guid>> LinkOrphanAttachmentsAsync(IEnumerable<Guid> mediaIds, Guid articleId, string? uploadedBy, long lamportTs, Guid? sourceNodeId)
    {
        using var conn = OpenConnection();
        var ids = mediaIds.ToList();
        var linked = await conn.QueryAsync<string>(
            @"UPDATE tbl_media
              SET article_id = @articleId, lamport_ts = @lamportTs, source_node_id = @sourceNodeId,
                  uploaded_by = NULL
              WHERE id IN @ids AND article_id IS NULL AND status = 'A' AND kind = 'attachment'
                AND (@uploadedBy IS NULL OR uploaded_by = @uploadedBy)
              RETURNING id",
            new { ids, articleId, uploadedBy, lamportTs, sourceNodeId });
        return linked.Select(Guid.Parse).ToList();
    }

    public async Task<Media?> GetOwnedOrphanAsync(Guid id, string uploadedBy)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Media>(
            $@"SELECT {SelectCols} FROM tbl_media m
               WHERE m.id = @id AND m.article_id IS NULL AND m.status = 'A' AND m.uploaded_by = @uploadedBy",
            new { id, uploadedBy });
    }

    public async Task<bool> SoftDeleteOwnedOrphanAsync(Guid id, string uploadedBy, System.Data.IDbTransaction transaction)
    {
        var now = UtcNow();
        return await transaction.Connection!.ExecuteAsync(
            @"UPDATE tbl_media SET status = 'D', deleted_at = @now
              WHERE id = @id AND article_id IS NULL AND status = 'A' AND uploaded_by = @uploadedBy",
            new { id, uploadedBy, now }, transaction) > 0;
    }
}

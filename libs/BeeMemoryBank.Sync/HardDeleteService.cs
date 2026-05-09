using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public class HardDeleteService(
    DbConnectionFactory factory,
    IEventLogger eventLogger,
    ILamportClock clock,
    INodeIdentityRepository nodeRepo,
    MediaStorageOptions mediaOpts,
    ILogger<HardDeleteService>? logger = null)
{
    public async Task<PagedList<HardDeleteListItem>> ListAsync(int page, int pageSize, string? filter, HardDeleteStatusFilter status, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var offset = (page - 1) * pageSize;

        using var conn = factory.CreateConnection();

        var statusFilter = status switch
        {
            HardDeleteStatusFilter.ActiveOnly => "AND status = 'A'",
            HardDeleteStatusFilter.DeletedOnly => "AND status = 'D'",
            _ => ""
        };

        var hasFilter = !string.IsNullOrWhiteSpace(filter);
        var folderSearch = hasFilter ? "AND (path LIKE @f OR name LIKE @f)" : "";
        var articleSearch = hasFilter ? "AND (tree_path LIKE @f OR title LIKE @f)" : "";
        var f = $"%{filter}%";

        var sql = $@"
            SELECT * FROM (
                SELECT
                    'folder' AS Type,
                    id AS Id,
                    path AS Path,
                    name AS Title,
                    status AS Status,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt,
                    0 AS Size
                FROM tbl_folder
                WHERE 1=1 {statusFilter} {folderSearch}

                UNION ALL

                SELECT
                    'article' AS Type,
                    a.id AS Id,
                    a.tree_path AS Path,
                    a.title AS Title,
                    a.status AS Status,
                    a.created_at AS CreatedAt,
                    a.updated_at AS UpdatedAt,
                    COALESCE(length(b.ciphertext), 0) AS Size
                FROM tbl_article a
                LEFT JOIN tbl_article_body b ON a.id = b.article_id
                WHERE 1=1 {statusFilter} {articleSearch}
            )
            ORDER BY Path ASC, Type ASC
            LIMIT @pageSize OFFSET @offset";

        var countSql = $@"
            SELECT (
                SELECT COUNT(*) FROM tbl_folder WHERE 1=1 {statusFilter} {folderSearch}
            ) + (
                SELECT COUNT(*) FROM tbl_article WHERE 1=1 {statusFilter} {articleSearch}
            )";

        var items = (await conn.QueryAsync<HardDeleteListItem>(sql, new { pageSize, offset, f })).ToList();
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, new { f });

        return new PagedList<HardDeleteListItem>(items, totalCount, page, pageSize);
    }

    public async Task<HardDeleteResult> DeleteArticleAsync(Guid articleId, int? userId, int? agentId, CancellationToken ct)
    {
        using var conn = factory.CreateConnection();
        conn.Open();
        using var trans = conn.BeginTransaction();

        List<Guid> mediaIds;
        try
        {
            var title = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT title FROM tbl_article WHERE id = @articleId", new { articleId }, trans);
            if (title == null) return new HardDeleteResult(0, 0, 0);

            mediaIds = await PurgeArticleRowsAsync(conn, trans, articleId);

            var identity = await nodeRepo.GetAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO tbl_hard_delete_audit
                (occurred_at, user_id, agent_id, source_node_id, entity_type, entity_identifier, entity_title, deleted_articles, deleted_media, lamport_ts)
                VALUES (@now, @userId, @agentId, @nodeId, 'article', @articleId, @title, 1, @mediaCount, @lamportTs)",
                new { now = DateTime.UtcNow, userId, agentId, nodeId = identity?.NodeId,
                      articleId = articleId.ToString(), title, mediaCount = mediaIds.Count,
                      lamportTs = clock.Tick() }, trans);

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }

        await TryLogHardDeleteAsync("article", articleId.ToString());
        DeleteMediaFiles(mediaIds);
        return new HardDeleteResult(1, 0, mediaIds.Count);
    }

    public async Task<HardDeleteResult> DeleteFolderAsync(string folderPath, int? userId, int? agentId, CancellationToken ct)
    {
        using var conn = factory.CreateConnection();
        conn.Open();
        using var trans = conn.BeginTransaction();

        int artCount, fldCount;
        List<Guid> allMediaToDelete;
        try
        {
            (artCount, fldCount, allMediaToDelete) = await PurgeFolderSubtreeAsync(conn, trans, folderPath);

            var folder = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT name FROM tbl_folder WHERE path = @folderPath", new { folderPath }, trans);

            var identity = await nodeRepo.GetAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO tbl_hard_delete_audit
                (occurred_at, user_id, agent_id, source_node_id, entity_type, entity_identifier, entity_title, deleted_articles, deleted_folders, deleted_media, lamport_ts)
                VALUES (@now, @userId, @agentId, @nodeId, 'folder', @folderPath, @title, @artCount, @fldCount, @medCount, @lamportTs)",
                new { now = DateTime.UtcNow, userId, agentId, nodeId = identity?.NodeId,
                      folderPath, title = folder ?? folderPath,
                      artCount, fldCount, medCount = allMediaToDelete.Count,
                      lamportTs = clock.Tick() }, trans);

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }

        await TryLogHardDeleteAsync("folder", folderPath);
        DeleteMediaFiles(allMediaToDelete);
        return new HardDeleteResult(artCount, fldCount, allMediaToDelete.Count);
    }

    public async Task ApplyRemoteAsync(HardDeleteEventPayload payload, long lamportTs, Guid? sourceNodeId, CancellationToken ct)
    {
        if (payload.EntityType == "article")
        {
            if (Guid.TryParse(payload.EntityIdentifier, out var articleId))
                await DeleteArticleInternalAsync(articleId, sourceNodeId, lamportTs, ct);
        }
        else if (payload.EntityType == "folder")
        {
            await DeleteFolderInternalAsync(payload.EntityIdentifier, sourceNodeId, lamportTs, ct);
        }
    }

    private async Task DeleteArticleInternalAsync(Guid articleId, Guid? sourceNodeId, long lamportTs, CancellationToken ct)
    {
        using var conn = factory.CreateConnection();
        conn.Open();
        using var trans = conn.BeginTransaction();

        List<Guid> mediaIds;
        try
        {
            var title = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT title FROM tbl_article WHERE id = @articleId", new { articleId }, trans);
            if (title == null) return;

            mediaIds = await PurgeArticleRowsAsync(conn, trans, articleId);

            await conn.ExecuteAsync(@"
                INSERT INTO tbl_hard_delete_audit
                (occurred_at, source_node_id, entity_type, entity_identifier, entity_title, deleted_articles, deleted_media, lamport_ts)
                VALUES (@now, @nodeId, 'article', @articleId, @title, 1, @mediaCount, @lamportTs)",
                new { now = DateTime.UtcNow, nodeId = sourceNodeId,
                      articleId = articleId.ToString(), title, mediaCount = mediaIds.Count, lamportTs }, trans);

            trans.Commit();
        }
        catch { trans.Rollback(); throw; }

        DeleteMediaFiles(mediaIds);
    }

    private async Task DeleteFolderInternalAsync(string folderPath, Guid? sourceNodeId, long lamportTs, CancellationToken ct)
    {
        using var conn = factory.CreateConnection();
        conn.Open();
        using var trans = conn.BeginTransaction();

        int artCount, fldCount;
        List<Guid> allMediaToDelete;
        try
        {
            (artCount, fldCount, allMediaToDelete) = await PurgeFolderSubtreeAsync(conn, trans, folderPath);

            var folder = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT name FROM tbl_folder WHERE path = @folderPath", new { folderPath }, trans);

            await conn.ExecuteAsync(@"
                INSERT INTO tbl_hard_delete_audit
                (occurred_at, source_node_id, entity_type, entity_identifier, entity_title, deleted_articles, deleted_folders, deleted_media, lamport_ts)
                VALUES (@now, @nodeId, 'folder', @folderPath, @title, @artCount, @fldCount, @medCount, @lamportTs)",
                new { now = DateTime.UtcNow, nodeId = sourceNodeId,
                      folderPath, title = folder ?? folderPath,
                      artCount, fldCount, medCount = allMediaToDelete.Count, lamportTs }, trans);

            trans.Commit();
        }
        catch { trans.Rollback(); throw; }

        DeleteMediaFiles(allMediaToDelete);
    }

    // Deletes all per-article rows (versions, tags, comments, conflicts, body, tombstone, media, article).
    // Returns the media IDs that were associated, so the caller can remove the .enc files post-commit.
    private static async Task<List<Guid>> PurgeArticleRowsAsync(IDbConnection conn, IDbTransaction trans, Guid articleId)
    {
        var mediaIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM tbl_media WHERE article_id = @articleId", new { articleId }, trans)).ToList();

        await conn.ExecuteAsync("DELETE FROM tbl_article_version WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_article_concept_tag WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_comment WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_conflict_version WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_article_body WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_tombstone WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_media WHERE article_id = @articleId", new { articleId }, trans);
        await conn.ExecuteAsync("DELETE FROM tbl_article WHERE id = @articleId", new { articleId }, trans);

        return mediaIds;
    }

    private static async Task<(int articleCount, int folderCount, List<Guid> mediaIds)> PurgeFolderSubtreeAsync(
        IDbConnection conn, IDbTransaction trans, string folderPath)
    {
        var prefix = folderPath.TrimEnd('/') + "/%";

        var articleIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM tbl_article WHERE tree_path = @folderPath OR tree_path LIKE @prefix",
            new { folderPath, prefix }, trans)).ToList();

        var folderIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM tbl_folder WHERE path = @folderPath OR path LIKE @prefix",
            new { folderPath, prefix }, trans)).ToList();

        var allMediaToDelete = new List<Guid>();
        foreach (var aid in articleIds)
        {
            var mediaIds = await PurgeArticleRowsAsync(conn, trans, aid);
            allMediaToDelete.AddRange(mediaIds);
        }

        foreach (var fid in folderIds)
        {
            await conn.ExecuteAsync("DELETE FROM tbl_folder_acl_entry WHERE folder_id = @fid", new { fid }, trans);
            await conn.ExecuteAsync("DELETE FROM tbl_folder WHERE id = @fid", new { fid }, trans);
        }

        return (articleIds.Count, folderIds.Count, allMediaToDelete);
    }

    private async Task TryLogHardDeleteAsync(string entityType, string entityIdentifier)
    {
        try
        {
            await eventLogger.LogHardDeleteAsync(entityType, entityIdentifier);
        }
        catch (Exception ex)
        {
            // DB rows are already purged; if sync logging fails, other nodes won't learn about
            // the deletion until a manual resync. Log loudly so operators notice.
            logger?.LogError(ex, "Hard-delete committed but sync event logging failed for {EntityType} {EntityIdentifier}",
                entityType, entityIdentifier);
        }
    }

    private void DeleteMediaFiles(List<Guid> mediaIds)
    {
        foreach (var mid in mediaIds)
        {
            var path = Path.Combine(mediaOpts.MediaDir, $"{mid}.enc");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to delete media file {Path}", path);
            }
        }
    }

    public async Task<HardDeletePreview> PreviewFolderAsync(string folderPath, CancellationToken ct)
    {
        using var conn = factory.CreateConnection();
        var prefix = folderPath.TrimEnd('/') + "/%";

        var artCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_article WHERE tree_path = @folderPath OR tree_path LIKE @prefix",
            new { folderPath, prefix });

        var fldCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_folder WHERE path = @folderPath OR path LIKE @prefix",
            new { folderPath, prefix });

        var medCount = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM tbl_media
              WHERE article_id IN (
                SELECT id FROM tbl_article WHERE tree_path = @folderPath OR tree_path LIKE @prefix
              )",
            new { folderPath, prefix });

        return new HardDeletePreview(artCount, fldCount, medCount);
    }

    public async Task<PagedList<HardDeleteAuditEntry>> ListAuditAsync(int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var offset = (page - 1) * pageSize;

        using var conn = factory.CreateConnection();
        var items = (await conn.QueryAsync<HardDeleteAuditEntry>(
            @"SELECT
                id AS Id,
                occurred_at AS OccurredAt,
                user_id AS UserId,
                agent_id AS AgentId,
                source_node_id AS SourceNodeId,
                entity_type AS EntityType,
                entity_identifier AS EntityIdentifier,
                entity_title AS EntityTitle,
                deleted_articles AS DeletedArticles,
                deleted_folders AS DeletedFolders,
                deleted_media AS DeletedMedia
              FROM tbl_hard_delete_audit
              ORDER BY id DESC LIMIT @pageSize OFFSET @offset",
            new { pageSize, offset })).ToList();

        var totalCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_hard_delete_audit");

        return new PagedList<HardDeleteAuditEntry>(items, totalCount, page, pageSize);
    }
}

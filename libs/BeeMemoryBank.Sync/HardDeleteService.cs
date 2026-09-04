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
                    COALESCE(bl.size, 0) AS Size
                FROM tbl_article a
                LEFT JOIN tbl_article_body b ON a.id = b.article_id
                LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
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
        List<Guid> purgedArticleIds;
        try
        {
            (artCount, fldCount, allMediaToDelete, purgedArticleIds) = await PurgeFolderSubtreeAsync(conn, trans, folderPath);

            var folder = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT name FROM tbl_folder WHERE path = @folderPath", new { folderPath }, trans);

            var identity = await nodeRepo.GetAsync();
            var lamportTs = clock.Tick();

            await conn.ExecuteAsync(@"
                INSERT INTO tbl_hard_delete_audit
                (occurred_at, user_id, agent_id, source_node_id, entity_type, entity_identifier, entity_title, deleted_articles, deleted_folders, deleted_media, lamport_ts)
                VALUES (@now, @userId, @agentId, @nodeId, 'folder', @folderPath, @title, @artCount, @fldCount, @medCount, @lamportTs)",
                new { now = DateTime.UtcNow, userId, agentId, nodeId = identity?.NodeId,
                      folderPath, title = folder ?? folderPath,
                      artCount, fldCount, medCount = allMediaToDelete.Count,
                      lamportTs }, trans);

            // See InsertArticlePurgeAuditRowsAsync below for why this second insert exists: the
            // single audit row above is keyed by the folder's PATH, but an article event's entity
            // id (EventEntityId.Derive) is always the article's own GUID, never a folder path. Without
            // one audit row per purged article id, an offline peer's queued edit for one of these
            // articles sails straight through IsHardDeletedAsync's gate -- which can only match
            // entity_identifier against rows that actually name that identifier -- and recreates
            // the article (and re-vivifies the folder row along with it) once it syncs back in.
            await InsertArticlePurgeAuditRowsAsync(conn, trans, purgedArticleIds, identity?.NodeId, userId, agentId, lamportTs);

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
        List<Guid> purgedArticleIds;
        try
        {
            (artCount, fldCount, allMediaToDelete, purgedArticleIds) = await PurgeFolderSubtreeAsync(conn, trans, folderPath);

            var folder = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT name FROM tbl_folder WHERE path = @folderPath", new { folderPath }, trans);

            await conn.ExecuteAsync(@"
                INSERT INTO tbl_hard_delete_audit
                (occurred_at, source_node_id, entity_type, entity_identifier, entity_title, deleted_articles, deleted_folders, deleted_media, lamport_ts)
                VALUES (@now, @nodeId, 'folder', @folderPath, @title, @artCount, @fldCount, @medCount, @lamportTs)",
                new { now = DateTime.UtcNow, nodeId = sourceNodeId,
                      folderPath, title = folder ?? folderPath,
                      artCount, fldCount, medCount = allMediaToDelete.Count, lamportTs }, trans);

            // A peer applying a remote folder purge must end up with exactly the same audit rows
            // as the node that originated it, or the two nodes disagree on which article ids are
            // gated and re-diverge the next time either applies a third node's queued edit. Use the
            // lamport_ts carried on the incoming event here -- NOT a fresh clock.Tick() -- for the
            // same reason the folder row above does: every node that applies this hard delete has
            // to agree on the one timestamp the gate compares against.
            await InsertArticlePurgeAuditRowsAsync(conn, trans, purgedArticleIds, sourceNodeId, null, null, lamportTs);

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

    /// <summary>
    /// Escapes the LIKE wildcards "%" and "_" (and the escape character itself) so a folder path
    /// is matched literally. Mirrors FolderRepository.EscapeLike; every LIKE that consumes it must
    /// declare ESCAPE '\'.
    /// </summary>
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task<(int articleCount, int folderCount, List<Guid> mediaIds, List<Guid> articleIds)> PurgeFolderSubtreeAsync(
        IDbConnection conn, IDbTransaction trans, string folderPath)
    {
        // The path is escaped and the LIKE declares its ESCAPE character. Folder names are user
        // text and SQLite's LIKE treats "_" as "any one character": purging "/Q_1" without this
        // also purged "/Q21/..." and everything else that matched the pattern -- a hard delete,
        // which physically removes rows and their media files with nothing to restore from.
        // FolderRepository has carried the same escaping for the same reason; this call site
        // predates it.
        var prefix = EscapeLike(folderPath.TrimEnd('/')) + "/%";

        var articleIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM tbl_article WHERE tree_path = @folderPath OR tree_path LIKE @prefix ESCAPE '\\'",
            new { folderPath, prefix }, trans)).ToList();

        var folderIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM tbl_folder WHERE path = @folderPath OR path LIKE @prefix ESCAPE '\\'",
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

        return (articleIds.Count, folderIds.Count, allMediaToDelete, articleIds);
    }

    /// <summary>
    /// Writes one <c>tbl_hard_delete_audit</c> row per purged article id, alongside (not instead
    /// of) the single folder-path row the caller already writes. This is the folder-purge half of
    /// the hard-delete gate: <see cref="EventEntityId.Derive"/> makes an ordinary article event's
    /// entity id the article's own GUID -- never the folder path it happened to live under -- so
    /// <c>EventLogRepository.IsHardDeletedAsync</c>'s "entity_identifier = @entityId" lookup can
    /// only catch a resurrecting event if a row exists that names that exact article id. Without
    /// this, a peer that was offline when a folder was purged and had a queued edit for one of the
    /// purged articles would sail through the gate -- its entity id was never audited -- and the
    /// applier would recreate the article (and re-vivify the folder row along with it).
    ///
    /// Callers pass the SAME lamport_ts they used for the folder row and run this in the SAME
    /// transaction, so a purge is all-or-nothing and every gated article agrees with the folder
    /// row on the exact timestamp the gate compares against. entity_title is left null: these rows
    /// exist purely to gate sync, not to be read by a human in the hard-delete audit UI, and
    /// fetching a title for what can be hundreds of purged articles is a needless round trip for a
    /// column nobody will look at.
    ///
    /// tbl_hard_delete_audit carries no uniqueness constraint on entity_identifier (only the
    /// non-unique idx_hard_delete_audit_entity index used by the gate's lookup), so inserting one
    /// row per article id here cannot collide with anything -- including the article's own
    /// single-article hard-delete row, which can never coexist with this path since that path
    /// requires the article to already exist and this one has just deleted it.
    /// </summary>
    private static Task InsertArticlePurgeAuditRowsAsync(
        IDbConnection conn, IDbTransaction trans, IReadOnlyCollection<Guid> articleIds,
        Guid? nodeId, int? userId, int? agentId, long lamportTs)
    {
        if (articleIds.Count == 0) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var rows = articleIds.Select(id => new
        {
            now,
            userId,
            agentId,
            nodeId,
            articleId = id.ToString(),
            lamportTs
        });

        return conn.ExecuteAsync(@"
            INSERT INTO tbl_hard_delete_audit
            (occurred_at, user_id, agent_id, source_node_id, entity_type, entity_identifier, deleted_articles, lamport_ts)
            VALUES (@now, @userId, @agentId, @nodeId, 'article', @articleId, 1, @lamportTs)",
            rows, trans);
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

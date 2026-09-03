using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class FolderRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder) : BaseRepository(factory), IFolderRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;
    private const string SelectCols = @"
        f.id              AS Id,
        f.path            AS Path,
        f.name            AS Name,
        f.parent_path     AS ParentPath,
        f.status          AS Status,
        f.lamport_ts      AS LamportTs,
        f.source_node_id  AS SourceNodeId,
        f.created_at      AS CreatedAt,
        f.updated_at      AS UpdatedAt,
        f.deleted_at      AS DeletedAt,
        f.cascade_delete_op_id AS CascadeDeleteOpId,
        f.is_system       AS IsSystem,
        f.remote_subscription_id AS RemoteSubscriptionId,
        f.remote_origin_id AS RemoteOriginId";

    public async Task<Folder?> GetByIdAsync(Guid id, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        var sql = includeDeleted
            ? $"SELECT {SelectCols} FROM tbl_folder f WHERE f.id = @id"
            : $"SELECT {SelectCols} FROM tbl_folder f WHERE f.id = @id AND f.status = 'A'";
        var folder = await conn.QuerySingleOrDefaultAsync<Folder>(sql, new { id });
        if (folder != null && _holder.Scope.IsAccessDenied(folder.Path)) return null;
        return folder;
    }

    public async Task<Folder?> GetByPathAsync(string path)
    {
        using var conn = OpenConnection();
        var folder = await conn.QuerySingleOrDefaultAsync<Folder>(
            $"SELECT {SelectCols} FROM tbl_folder f WHERE f.path = @path AND f.status = 'A'",
            new { path });
        if (folder != null && _holder.Scope.IsAccessDenied(folder.Path)) return null;
        return folder;
    }

    public async Task<List<Folder>> GetChildrenAsync(string? parentPath)
    {
        using var conn = OpenConnection();
        IEnumerable<Folder> result;
        if (parentPath == null)
        {
            result = await conn.QueryAsync<Folder>(
                $"SELECT {SelectCols} FROM tbl_folder f WHERE f.parent_path IS NULL AND f.status = 'A' ORDER BY (substr(f.name,1,1)='_') DESC, f.name",
                null);
        }
        else
        {
            result = await conn.QueryAsync<Folder>(
                $"SELECT {SelectCols} FROM tbl_folder f WHERE f.parent_path = @parentPath AND f.status = 'A' ORDER BY (substr(f.name,1,1)='_') DESC, f.name",
                new { parentPath });
        }
        return _holder.Scope.FilterFolders(result.ToList());
    }

    public async Task<List<Folder>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        var folders = (await conn.QueryAsync<Folder>(
            $"SELECT {SelectCols} FROM tbl_folder f WHERE f.status = 'A' ORDER BY f.path")).ToList();
        return _holder.Scope.FilterFolders(folders);
    }

    public async Task<int> CountAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_folder");
    }

    public async Task CreateAsync(Folder folder)
    {
        // Repo-level write guard: close the "new endpoint forgets manual ACL check" hole.
        if (_holder.Scope.IsAccessDenied(folder.Path))
            throw new UnauthorizedAccessException($"Write access denied for path '{folder.Path}'");
        if (_holder.Scope.IsReadOnly(folder.Path))
            throw new ReadOnlyAccessException(folder.Path);

        // Defence-in-depth: even sync / import / migration paths that bypass
        // FolderService must not be able to fabricate a reserved system path
        // without setting IsSystem. Disallow the combination explicitly.
        if (!folder.IsSystem && SystemFolders.IsReservedSystemPath(folder.Path))
            throw new InvalidOperationException(
                $"Folder path '{folder.Path}' is reserved for the system. Use FolderService.EnsureSystemFolderAsync.");

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_folder
              (id, path, name, parent_path, status, lamport_ts, source_node_id, created_at, updated_at, deleted_at, cascade_delete_op_id, is_system, remote_subscription_id, remote_origin_id)
              VALUES (@Id, @Path, @Name, @ParentPath, @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt, @DeletedAt, @CascadeDeleteOpId, @IsSystem, @RemoteSubscriptionId, @RemoteOriginId)",
            new
            {
                folder.Id, folder.Path, folder.Name, folder.ParentPath, folder.Status,
                folder.LamportTs, folder.SourceNodeId, folder.CreatedAt, folder.UpdatedAt,
                folder.DeletedAt, folder.CascadeDeleteOpId,
                IsSystem = folder.IsSystem ? 1 : 0,
                folder.RemoteSubscriptionId,
                folder.RemoteOriginId
            });
    }

    public async Task UpdateAsync(Folder folder)
    {
        if (_holder.Scope.IsAccessDenied(folder.Path))
            throw new UnauthorizedAccessException($"Write access denied for path '{folder.Path}'");
        if (_holder.Scope.IsReadOnly(folder.Path))
            throw new ReadOnlyAccessException(folder.Path);

        // SECURITY: consult the stored row, not the caller-supplied object — a
        // malicious or buggy caller could clear RemoteSubscriptionId on the
        // in-memory Folder to bypass the read-only guard. Same fix pattern as
        // ArticleRepository.UpdateAsync (gemini+kilo round-3 finding).
        if (!_holder.Scope.IsSuperadmin)
        {
            using var check = OpenConnection();
            var storedRemoteSubId = await check.QuerySingleOrDefaultAsync<string?>(
                "SELECT remote_subscription_id FROM tbl_folder WHERE id = @id",
                new { id = folder.Id });
            if (!string.IsNullOrEmpty(storedRemoteSubId))
                throw new ReadOnlyAccessException($"Folder '{folder.Path}' belongs to a remote read-only share.");
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_folder
              SET path = @Path, name = @Name, parent_path = @ParentPath,
                  status = @Status, lamport_ts = @LamportTs, source_node_id = @SourceNodeId,
                  updated_at = @UpdatedAt, deleted_at = @DeletedAt,
                  cascade_delete_op_id = @CascadeDeleteOpId,
                  is_system = @IsSystem,
                  remote_subscription_id = @RemoteSubscriptionId,
                  remote_origin_id = @RemoteOriginId
              WHERE id = @Id",
            new
            {
                folder.Id, folder.Path, folder.Name, folder.ParentPath, folder.Status,
                folder.LamportTs, folder.SourceNodeId, folder.UpdatedAt,
                folder.DeletedAt, folder.CascadeDeleteOpId,
                IsSystem = folder.IsSystem ? 1 : 0,
                folder.RemoteSubscriptionId,
                folder.RemoteOriginId
            });
    }

    public async Task SoftDeleteAsync(Guid id, DateTime deletedAt, Guid? cascadeOpId = null)
    {
        if (!_holder.Scope.IsSuperadmin)
        {
            using var check = OpenConnection();
            var path = await check.QuerySingleOrDefaultAsync<string?>(
                "SELECT path FROM tbl_folder WHERE id = @id", new { id });
            if (path != null)
            {
                if (_holder.Scope.IsAccessDenied(path))
                    throw new UnauthorizedAccessException($"Write access denied for path '{path}'");
                if (_holder.Scope.IsReadOnly(path))
                    throw new ReadOnlyAccessException(path);
            }
        }

        using var conn = OpenConnection();
        var now = deletedAt.ToString("o");
        // Bind cascadeOpId as Guid? (not .ToString()) so it normalizes to uppercase TEXT
        // and matches rows written via tbl_folder.CreateAsync/UpdateAsync, which bind the
        // Folder.CascadeDeleteOpId property via Dapper's handler.
        await conn.ExecuteAsync(
            "UPDATE tbl_folder SET status = 'D', deleted_at = @now, updated_at = @now, cascade_delete_op_id = @cascadeOpId WHERE id = @id AND status = 'A'",
            new { id, now, cascadeOpId });
    }

    public async Task<int> SoftDeleteByPathPrefixAsync(string pathPrefix, DateTime deletedAt, Guid? cascadeOpId = null)
    {
        if (_holder.Scope.IsAccessDenied(pathPrefix))
            throw new UnauthorizedAccessException($"Write access denied for path '{pathPrefix}'");
        if (_holder.Scope.IsReadOnly(pathPrefix))
            throw new ReadOnlyAccessException(pathPrefix);

        // H1: the two checks above only cover pathPrefix itself. A caller can be authorized on the
        // TOP of a subtree (e.g. allow=/, deny=/Work/Secret) while a DESCENDANT under it is
        // individually denied or read-only — this cascading soft-delete must not silently sweep
        // that descendant along for the ride just because the top of the subtree passed. Walk the
        // real (unfiltered) descendant paths and re-check every single one before touching
        // anything. Skipped for superadmins/System scope, where every check below is a guaranteed
        // no-op anyway — no need to pay for the extra query.
        await ThrowIfAnyDescendantWriteDeniedAsync(pathPrefix);

        using var conn = OpenConnection();
        var now = deletedAt.ToString("o");
        var prefix = EscapeLike(pathPrefix.TrimEnd('/') + "/") + "%";
        return await conn.ExecuteAsync(
            "UPDATE tbl_folder SET status = 'D', deleted_at = @now, updated_at = @now, cascade_delete_op_id = @cascadeOpId WHERE path LIKE @prefix ESCAPE '\\' AND status = 'A'",
            new { prefix, now, cascadeOpId });
    }

    /// <summary>
    /// H1: throws <see cref="UnauthorizedAccessException"/> or <see cref="ReadOnlyAccessException"/>
    /// if any ACTIVE folder strictly under <paramref name="pathPrefix"/> is denied or read-only for
    /// the ambient caller scope. Reads the raw path list directly (no ACL filter applied to the
    /// query itself — that's the point: we need the true descendant set, not what the caller can
    /// see) and then re-checks each path against the same per-path guards every other write method
    /// here uses, so the two never drift out of sync.
    /// </summary>
    public async Task ThrowIfAnyDescendantWriteDeniedAsync(string pathPrefix)
    {
        // Skipped for superadmin/System scope, where every per-path check below is a guaranteed
        // no-op — no need to pay for the extra query. Lives here rather than at the call sites so
        // every caller gets the same behaviour.
        if (_holder.Scope.IsSuperadmin)
            return;

        using var conn = OpenConnection();
        var prefix = EscapeLike(pathPrefix.TrimEnd('/') + "/") + "%";
        var descendantPaths = await conn.QueryAsync<string>(
            "SELECT path FROM tbl_folder WHERE path LIKE @prefix ESCAPE '\\' AND status = 'A'",
            new { prefix });

        foreach (var path in descendantPaths)
        {
            if (_holder.Scope.IsAccessDenied(path))
                throw new UnauthorizedAccessException(
                    $"Write access denied for path '{path}' (descendant of '{pathPrefix}')");
            if (_holder.Scope.IsReadOnly(path))
                throw new ReadOnlyAccessException(path);
        }
    }

    public async Task<List<Folder>> ListSoftDeletedByCascadeOpIdAsync(Guid cascadeOpId, string pathPrefix)
    {
        using var conn = OpenConnection();
        var prefix = EscapeLike(pathPrefix.TrimEnd('/') + "/") + "%";
        var folders = (await conn.QueryAsync<Folder>(
            $@"SELECT {SelectCols} FROM tbl_folder f
               WHERE f.cascade_delete_op_id = @cascadeOpId
                 AND f.status = 'D'
                 AND f.path LIKE @prefix ESCAPE '\'
               ORDER BY length(f.path) ASC",
            new { cascadeOpId, prefix })).ToList();
        return folders;
    }

    public async Task<int> RenamePathAsync(string oldPath, string newPath, Guid folderId,
        long lamportTs, Guid? sourceNodeId, DateTime updatedAt)
    {
        if (_holder.Scope.IsAccessDenied(oldPath) || _holder.Scope.IsAccessDenied(newPath))
            throw new UnauthorizedAccessException($"Write access denied for rename '{oldPath}' -> '{newPath}'");
        if (_holder.Scope.IsReadOnly(oldPath))
            throw new ReadOnlyAccessException(oldPath);
        if (_holder.Scope.IsReadOnly(newPath))
            throw new ReadOnlyAccessException(newPath);

        using var conn = OpenConnection();
        var updatedAtStr = updatedAt.ToString("o");
        var newName = GetLastSegment(newPath);
        var newParentPath = GetParentPath(newPath);
        var oldPathPrefix = oldPath.TrimEnd('/') + "/";
        var oldPathLikePrefix = EscapeLike(oldPathPrefix) + "%";

        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(
                @"UPDATE tbl_folder
                  SET path = @newPath, name = @newName, parent_path = @newParentPath,
                      lamport_ts = @lamportTs, source_node_id = @sourceNodeId, updated_at = @updatedAtStr
                  WHERE id = @folderId",
                new { newPath, newName, newParentPath, lamportTs, sourceNodeId, updatedAtStr, folderId },
                tx);

            var childRows = await conn.ExecuteAsync(
                @"UPDATE tbl_folder
                  SET path = @newPath || SUBSTR(path, LENGTH(@oldPath) + 1),
                      parent_path = CASE
                          WHEN parent_path = @oldPath THEN @newPath
                          ELSE @newPath || SUBSTR(parent_path, LENGTH(@oldPath) + 1)
                      END,
                      updated_at = @updatedAtStr,
                      lamport_ts = @lamportTs,
                      source_node_id = @sourceNodeId
                  WHERE path LIKE @oldPathLikePrefix ESCAPE '\'",
                new { newPath, oldPath, oldPathLikePrefix, updatedAtStr, lamportTs, sourceNodeId },
                tx);

            var escapedOldPathExact = EscapeLike(oldPath);
            await conn.ExecuteAsync(
                @"UPDATE tbl_article
                  SET tree_path = @newPath || SUBSTR(tree_path, LENGTH(@oldPath) + 1)
                  WHERE tree_path = @oldPath OR tree_path LIKE @escapedOldPathExact || '/' || '%' ESCAPE '\'",
                new { newPath, oldPath, escapedOldPathExact },
                tx);

            tx.Commit();
            return childRows;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task EnsureExistsAsync(string path, Guid? sourceNodeId)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return;

        // Enforce, at the point that assumes it, the invariant the ancestor-vivification code
        // below states as given: "the leaf creation has already been authorized at the endpoint
        // level". It wasn't, on the busiest path in the app — ArticleService.CreateAsync (and the
        // move branch of UpdateAsync) vivified folders BEFORE articleRepo.CreateAsync applied the
        // caller's ACL, and nothing rolled the folders back when that throw came. So an agent
        // denied on /Secrets, or read-only on /Public, could call bee_save_article with
        // treePath=/Secrets/Anything and persist arbitrary folders there — plaintext metadata,
        // visible to everyone, and node-local since no FolderCreate event is emitted for them.
        //
        // Only the requested LEAF is checked. Ancestors deliberately are not: an AllowList user
        // creating /A/B/C must not be blocked because /A and /A/B are outside their scope — the
        // recursion below re-enters through EnsureExistsCoreAsync, not here, precisely so those
        // stubs stay creatable. System-scope callers (sync's EventApplier, the startup
        // FolderBootstrapper, background workers with no HttpContext) pass by construction.
        ThrowIfWriteDenied(path);

        await EnsureExistsCoreAsync(path, sourceNodeId);
    }

    public Task EnsureAncestorsExistAsync(string path, Guid? sourceNodeId)
        => EnsureExistsCoreAsync(path, sourceNodeId);

    public void ThrowIfWriteDenied(string? path)
    {
        if (_holder.Scope.IsAccessDenied(path))
            throw new UnauthorizedAccessException($"Write access denied for path '{path}'");
        if (_holder.Scope.IsReadOnly(path))
            throw new ReadOnlyAccessException(path);
    }

    private async Task EnsureExistsCoreAsync(string path, Guid? sourceNodeId)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return;

        // Raw existence check (no scope filter): an AllowList user creating
        // /A/B/C must not be blocked here just because /A and /A/B are hidden
        // from their scope — those ancestors already exist in the DB (an admin
        // created them when making /A/B reachable), so we only need to know if
        // the row is present, not whether this caller can read it.
        using (var conn = OpenConnection())
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM tbl_folder WHERE path = @path AND status = 'A'",
                new { path });
            if (exists > 0) return;
        }

        // Ensure all parents exist first (recurse up). Deliberately re-enters the Core method, not
        // the public one — ancestors are stubs the caller may well have no ACL for, and blocking
        // on them would break the AllowList case documented above.
        var parentPath = GetParentPath(path);
        if (parentPath != null)
            await EnsureExistsCoreAsync(parentPath, sourceNodeId);

        // Auto-creating a missing ancestor stub: bypass the repo-level write
        // guard by swapping scope to System for the Create call. The leaf
        // creation has already been authorized at the endpoint level.
        var previousScope = _holder.Scope;
        _holder.Scope = SystemCallerScope.Instance;
        var now = DateTime.UtcNow;
        try
        {
            await CreateAsync(new Folder
            {
                Id = Guid.NewGuid(),
                Path = path,
                Name = GetLastSegment(path),
                ParentPath = parentPath,
                Status = "A",
                LamportTs = 0,
                SourceNodeId = sourceNodeId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
        }
        finally
        {
            _holder.Scope = previousScope;
        }
    }

    public async Task<List<Guid>> ListIdsByPathPrefixAsync(string pathPrefix)
    {
        using var conn = OpenConnection();
        var prefix = EscapeLike(pathPrefix.TrimEnd('/') + "/") + "%";
        var ids = await conn.QueryAsync<Guid>(
            "SELECT id FROM tbl_folder WHERE path LIKE @prefix ESCAPE '\\' AND status = 'A'",
            new { prefix });
        return ids.ToList();
    }

    public async Task<List<Folder>> SearchAsync(string query)
    {
        // WP-07: FTS5-backed search over fts_folder (name + path), mirroring ArticleRepository.
        // Same tokenize→stem→quoted-prefix MATCH build; same empty-query short-circuit; same
        // status = 'A' re-filter at the join (soft-deleted folders linger in the FTS index).
        // bm25 weights name above path; the underscore-prefix-sorts-first quirk stays primary.
        var matchExpr = FtsQueryBuilder.BuildMatchExpression(query);
        if (matchExpr == null)
        {
            return [];
        }

        using var conn = OpenConnection();
        var sql = $@"WITH folder_hits AS (
                       SELECT fld.id AS id, bm25(fts_folder, 10.0, 2.0) AS score
                       FROM fts_folder
                       JOIN tbl_folder fld ON fld.rowid = fts_folder.rowid
                       WHERE fts_folder MATCH @matchExpr AND fld.status = 'A'
                     )
                     SELECT {SelectCols}
                     FROM tbl_folder f
                     JOIN folder_hits fh ON fh.id = f.id
                     WHERE f.status = 'A'
                     ORDER BY (substr(f.name,1,1)='_') DESC, fh.score ASC, f.name";
        var folders = (await conn.QueryAsync<Folder>(sql, new { matchExpr })).ToList();
        return _holder.Scope.FilterFolders(folders);
    }

    /// <summary>
    /// The pre-WP-07 <see cref="SearchAsync"/> implementation: a per-row managed-code
    /// <c>unicode_contains</c> substring scan over name and path, no morphology. Kept available
    /// (currently unused by <c>SearchService</c>) for a possible future "exact substring" search
    /// mode. Wiring a UI/API toggle for it is out of WP-07's scope.
    /// </summary>
    public async Task<List<Folder>> SearchByExactSubstringAsync(string query)
    {
        using var conn = OpenConnection();
        var sql = $"SELECT {SelectCols} FROM tbl_folder f WHERE f.status = 'A' AND (unicode_contains(f.name, @query) OR unicode_contains(f.path, @query)) ORDER BY (substr(f.name,1,1)='_') DESC, f.name";
        var folders = (await conn.QueryAsync<Folder>(sql, new { query })).ToList();
        return _holder.Scope.FilterFolders(folders);
    }

    private static string? GetParentPath(string path)
    {
        if (path == "/") return null;
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? null : trimmed[..idx];
    }

    private static string GetLastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed.TrimStart('/') : trimmed[(idx + 1)..];
    }

    private static string EscapeLike(string s)
    {
        return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}

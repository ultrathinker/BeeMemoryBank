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
        f.cascade_delete_op_id AS CascadeDeleteOpId";

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

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_folder
              (id, path, name, parent_path, status, lamport_ts, source_node_id, created_at, updated_at, deleted_at, cascade_delete_op_id)
              VALUES (@Id, @Path, @Name, @ParentPath, @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt, @DeletedAt, @CascadeDeleteOpId)",
            folder);
    }

    public async Task UpdateAsync(Folder folder)
    {
        if (_holder.Scope.IsAccessDenied(folder.Path))
            throw new UnauthorizedAccessException($"Write access denied for path '{folder.Path}'");

        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_folder
              SET path = @Path, name = @Name, parent_path = @ParentPath,
                  status = @Status, lamport_ts = @LamportTs, source_node_id = @SourceNodeId,
                  updated_at = @UpdatedAt, deleted_at = @DeletedAt,
                  cascade_delete_op_id = @CascadeDeleteOpId
              WHERE id = @Id",
            folder);
    }

    public async Task SoftDeleteAsync(Guid id, DateTime deletedAt, Guid? cascadeOpId = null)
    {
        if (!_holder.Scope.IsSuperadmin)
        {
            using var check = OpenConnection();
            var path = await check.QuerySingleOrDefaultAsync<string?>(
                "SELECT path FROM tbl_folder WHERE id = @id", new { id });
            if (path != null && _holder.Scope.IsAccessDenied(path))
                throw new UnauthorizedAccessException($"Write access denied for path '{path}'");
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

        using var conn = OpenConnection();
        var now = deletedAt.ToString("o");
        var prefix = EscapeLike(pathPrefix.TrimEnd('/') + "/") + "%";
        return await conn.ExecuteAsync(
            "UPDATE tbl_folder SET status = 'D', deleted_at = @now, updated_at = @now, cascade_delete_op_id = @cascadeOpId WHERE path LIKE @prefix ESCAPE '\\' AND status = 'A'",
            new { prefix, now, cascadeOpId });
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

        // Ensure all parents exist first (recurse up)
        var parentPath = GetParentPath(path);
        if (parentPath != null)
            await EnsureExistsAsync(parentPath, sourceNodeId);

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

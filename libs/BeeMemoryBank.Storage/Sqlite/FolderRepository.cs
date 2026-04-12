using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class FolderRepository(DbConnectionFactory factory) : BaseRepository(factory), IFolderRepository
{
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
        f.deleted_at      AS DeletedAt";

    public async Task<Folder?> GetByIdAsync(Guid id, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        var sql = includeDeleted
            ? $"SELECT {SelectCols} FROM tbl_folder f WHERE f.id = @id"
            : $"SELECT {SelectCols} FROM tbl_folder f WHERE f.id = @id AND f.status = 'A'";
        return await conn.QuerySingleOrDefaultAsync<Folder>(sql, new { id });
    }

    public async Task<Folder?> GetByPathAsync(string path)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Folder>(
            $"SELECT {SelectCols} FROM tbl_folder f WHERE f.path = @path AND f.status = 'A'",
            new { path });
    }

    public async Task<List<Folder>> GetChildrenAsync(string? parentPath)
    {
        using var conn = OpenConnection();
        IEnumerable<Folder> result;
        if (parentPath == null)
        {
            result = await conn.QueryAsync<Folder>(
                $"SELECT {SelectCols} FROM tbl_folder f WHERE f.parent_path IS NULL AND f.status = 'A' ORDER BY f.name",
                null);
        }
        else
        {
            result = await conn.QueryAsync<Folder>(
                $"SELECT {SelectCols} FROM tbl_folder f WHERE f.parent_path = @parentPath AND f.status = 'A' ORDER BY f.name",
                new { parentPath });
        }
        return result.ToList();
    }

    public async Task<List<Folder>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Folder>(
            $"SELECT {SelectCols} FROM tbl_folder f WHERE f.status = 'A' ORDER BY f.path")).ToList();
    }

    public async Task<int> CountAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_folder");
    }

    public async Task CreateAsync(Folder folder)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_folder
              (id, path, name, parent_path, status, lamport_ts, source_node_id, created_at, updated_at, deleted_at)
              VALUES (@Id, @Path, @Name, @ParentPath, @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt, @DeletedAt)",
            folder);
    }

    public async Task UpdateAsync(Folder folder)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_folder
              SET path = @Path, name = @Name, parent_path = @ParentPath,
                  status = @Status, lamport_ts = @LamportTs, source_node_id = @SourceNodeId,
                  updated_at = @UpdatedAt, deleted_at = @DeletedAt
              WHERE id = @Id",
            folder);
    }

    public async Task SoftDeleteAsync(Guid id, DateTime deletedAt)
    {
        using var conn = OpenConnection();
        var now = deletedAt.ToString("o");
        await conn.ExecuteAsync(
            "UPDATE tbl_folder SET status = 'D', deleted_at = @now, updated_at = @now WHERE id = @id AND status = 'A'",
            new { id, now });
    }

    public async Task<int> SoftDeleteByPathPrefixAsync(string pathPrefix, DateTime deletedAt)
    {
        using var conn = OpenConnection();
        var now = deletedAt.ToString("o");
        var prefix = pathPrefix.TrimEnd('/') + "/";
        return await conn.ExecuteAsync(
            "UPDATE tbl_folder SET status = 'D', deleted_at = @now, updated_at = @now WHERE path LIKE @prefix || '%' AND status = 'A'",
            new { prefix, now });
    }

    public async Task<int> RenamePathAsync(string oldPath, string newPath, Guid folderId,
        long lamportTs, Guid? sourceNodeId, DateTime updatedAt)
    {
        using var conn = OpenConnection();
        var updatedAtStr = updatedAt.ToString("o");
        var newName = GetLastSegment(newPath);
        var newParentPath = GetParentPath(newPath);
        var oldPathPrefix = oldPath.TrimEnd('/') + "/%";

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
                  WHERE path LIKE @oldPathPrefix",
                new { newPath, oldPath, oldPathPrefix, updatedAtStr, lamportTs, sourceNodeId },
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

        // Ensure all parents exist first (recurse up)
        var parentPath = GetParentPath(path);
        if (parentPath != null)
            await EnsureExistsAsync(parentPath, sourceNodeId);

        var existing = await GetByPathAsync(path);
        if (existing != null) return;

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
    }

    public async Task<List<Folder>> SearchAsync(string query)
    {
        using var conn = OpenConnection();
        var sql = $"SELECT {SelectCols} FROM tbl_folder f WHERE f.status = 'A' AND (unicode_contains(f.name, @query) OR unicode_contains(f.path, @query)) ORDER BY f.name";
        return (await conn.QueryAsync<Folder>(sql, new { query })).ToList();
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
}

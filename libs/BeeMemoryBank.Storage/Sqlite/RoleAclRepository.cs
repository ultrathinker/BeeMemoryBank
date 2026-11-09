using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class RoleAclRepository(DbConnectionFactory factory) : BaseRepository(factory), IRoleAclRepository
{
    private const string SelectCols =
        @"role_name AS RoleName, folder_id AS FolderId, effect AS Effect,
          is_read_only AS IsReadOnly, created_at AS CreatedAt";

    public async Task<List<RoleAclEntry>> GetByRoleNameAsync(string roleName)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<RoleAclEntry>(
            $"SELECT {SelectCols} FROM tbl_role_folder_acl_entry WHERE role_name = @roleName",
            new { roleName })).ToList();
    }

    public async Task AddAsync(RoleAclEntry entry)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_role_folder_acl_entry (role_name, folder_id, effect, is_read_only, created_at)
              VALUES (@RoleName, @FolderId, @Effect, @IsReadOnly, @CreatedAt)",
            new
            {
                entry.RoleName,
                entry.FolderId,
                Effect = entry.Effect.ToString().ToLowerInvariant(),
                IsReadOnly = entry.IsReadOnly ? 1 : 0,
                entry.CreatedAt
            });
    }

    public async Task SetReadOnlyAsync(string roleName, Guid folderId, AclEffect effect, bool isReadOnly)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_role_folder_acl_entry
                 SET is_read_only = @IsReadOnly
               WHERE role_name = @roleName
                 AND folder_id  = @folderId
                 AND effect     = @effect",
            new
            {
                roleName,
                folderId,
                effect = effect.ToString().ToLowerInvariant(),
                IsReadOnly = isReadOnly ? 1 : 0
            });
    }

    public async Task RemoveByRoleAndFolderAsync(string roleName, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_role_folder_acl_entry WHERE role_name = @roleName AND folder_id = @folderId",
            new { roleName, folderId });
    }

    public async Task<List<string>> GetRoleNamesByFolderIdAsync(Guid folderId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<string>(
            "SELECT DISTINCT role_name FROM tbl_role_folder_acl_entry WHERE folder_id = @folderId",
            new { folderId })).ToList();
    }

    public async Task<List<FolderAclMarkRow>> ListMarkRowsAsync()
    {
        // The rule, its role and the folder's CURRENT path in one join. Soft-deleted folders are
        // left out, and so is the superadmin role (it bypasses folder rules; RoleService refuses to
        // add any, so a row there could only be stale).
        using var conn = OpenConnection();
        return (await conn.QueryAsync<FolderAclMarkRow>(
            @"SELECT 'role'         AS SubjectType,
                     r.name         AS SubjectId,
                     r.display_name AS SubjectName,
                     a.folder_id    AS FolderId,
                     f.path         AS FolderPath,
                     a.effect       AS Effect,
                     a.is_read_only AS IsReadOnly
                FROM tbl_role_folder_acl_entry a
                JOIN tbl_role   r ON r.name = a.role_name AND r.name <> @superadmin COLLATE NOCASE
                JOIN tbl_folder f ON f.id = a.folder_id AND f.status = 'A'
               ORDER BY f.path, r.display_name",
            new { superadmin = UserRoles.Superadmin })).ToList();
    }

    public async Task<Dictionary<string, int>> CountEntriesPerRoleAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<(string RoleName, int Count)>(
            "SELECT role_name AS RoleName, COUNT(*) AS Count FROM tbl_role_folder_acl_entry GROUP BY role_name COLLATE NOCASE");
        // Role names are compared case-insensitively everywhere else, so both the grouping and
        // the lookup this feeds must be too. The explicit COLLATE NOCASE above is belt-and-braces
        // over the column's own collation: without case-insensitive grouping SQLite would emit
        // one row per casing, and ToDictionary with an OrdinalIgnoreCase comparer would then
        // throw on the duplicate key rather than merely miscount.
        return rows.ToDictionary(r => r.RoleName, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }
}

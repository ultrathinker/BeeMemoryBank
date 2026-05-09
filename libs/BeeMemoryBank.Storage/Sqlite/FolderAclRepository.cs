using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class FolderAclRepository(DbConnectionFactory factory) : BaseRepository(factory), IFolderAclRepository
{
    private const string SelectCols =
        @"rowid AS Id, user_id AS UserId,
          folder_id AS FolderId, effect AS Effect, created_at AS CreatedAt";

    public async Task<List<FolderAclEntry>> GetByUserIdAsync(int userId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<FolderAclEntry>(
            $"SELECT {SelectCols} FROM tbl_folder_acl_entry WHERE user_id = @userId",
            new { userId })).ToList();
    }

    public async Task AddAsync(FolderAclEntry entry)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_folder_acl_entry (user_id, folder_id, effect, created_at)
              VALUES (@UserId, @FolderId, @Effect, @CreatedAt)",
            new { entry.UserId, entry.FolderId, Effect = entry.Effect.ToString().ToLowerInvariant(), entry.CreatedAt });
    }

    public async Task RemoveByUserFolderAndEffectAsync(int userId, Guid folderId, AclEffect effect)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_folder_acl_entry WHERE user_id = @userId AND folder_id = @folderId AND effect = @effect",
            new { userId, folderId, effect = effect.ToString().ToLowerInvariant() });
    }

    public async Task RemoveByUserAndFolderAsync(int userId, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_folder_acl_entry WHERE user_id = @userId AND folder_id = @folderId",
            new { userId, folderId });
    }

    public async Task<List<int>> GetUserIdsByFolderIdAsync(Guid folderId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<int>(
            "SELECT DISTINCT user_id FROM tbl_folder_acl_entry WHERE folder_id = @folderId",
            new { folderId })).ToList();
    }
}

using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class FolderRestrictionRepository(DbConnectionFactory factory) : BaseRepository(factory), IFolderRestrictionRepository
{
    private const string SelectCols =
        @"id AS Id, user_id AS UserId, agent_id AS AgentId,
          folder_id AS FolderId, created_at AS CreatedAt";

    public async Task<List<FolderRestriction>> GetByUserIdAsync(int userId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<FolderRestriction>(
            $"SELECT {SelectCols} FROM tbl_folder_restriction WHERE user_id = @userId",
            new { userId })).ToList();
    }

    public async Task<List<FolderRestriction>> GetByAgentIdAsync(int agentId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<FolderRestriction>(
            $"SELECT {SelectCols} FROM tbl_folder_restriction WHERE agent_id = @agentId",
            new { agentId })).ToList();
    }

    public async Task AddAsync(FolderRestriction restriction)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_folder_restriction (user_id, agent_id, folder_id, created_at)
              VALUES (@UserId, @AgentId, @FolderId, @CreatedAt)",
            restriction);
    }

    public async Task RemoveAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_folder_restriction WHERE id = @id",
            new { id });
    }

    public async Task RemoveByUserAndFolderAsync(int userId, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_folder_restriction WHERE user_id = @userId AND folder_id = @folderId",
            new { userId, folderId });
    }

    public async Task RemoveByAgentAndFolderAsync(int agentId, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_folder_restriction WHERE agent_id = @agentId AND folder_id = @folderId",
            new { agentId, folderId });
    }
}

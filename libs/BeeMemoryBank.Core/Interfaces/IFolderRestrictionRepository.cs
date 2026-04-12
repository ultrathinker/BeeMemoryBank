using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IFolderRestrictionRepository
{
    Task<List<FolderRestriction>> GetByUserIdAsync(int userId);
    Task<List<FolderRestriction>> GetByAgentIdAsync(int agentId);
    Task AddAsync(FolderRestriction restriction);
    Task RemoveAsync(int id);
    Task RemoveByUserAndFolderAsync(int userId, Guid folderId);
    Task RemoveByAgentAndFolderAsync(int agentId, Guid folderId);
}

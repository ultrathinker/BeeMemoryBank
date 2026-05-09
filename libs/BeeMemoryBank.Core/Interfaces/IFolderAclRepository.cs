using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IFolderAclRepository
{
    Task<List<FolderAclEntry>> GetByUserIdAsync(int userId);
    Task AddAsync(FolderAclEntry entry);
    Task RemoveByUserFolderAndEffectAsync(int userId, Guid folderId, AclEffect effect);
    Task RemoveByUserAndFolderAsync(int userId, Guid folderId);
    Task<List<int>> GetUserIdsByFolderIdAsync(Guid folderId);
}

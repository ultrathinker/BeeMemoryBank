using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IFolderAclRepository
{
    Task<List<FolderAclEntry>> GetByUserIdAsync(int userId);
    Task AddAsync(FolderAclEntry entry);
    Task RemoveByUserFolderAndEffectAsync(int userId, Guid folderId, AclEffect effect);
    Task RemoveByUserAndFolderAsync(int userId, Guid folderId);
    Task<List<int>> GetUserIdsByFolderIdAsync(Guid folderId);
    Task SetReadOnlyAsync(int userId, Guid folderId, AclEffect effect, bool isReadOnly);

    /// <summary>
    /// Every per-user rule on a live folder, for active non-superadmin users only, with the user's
    /// name and the folder's current path resolved by the same query (one round trip, no per-row
    /// lookups). Feeds the folder-marks overview; see <see cref="FolderAclMarkRow"/> for what is
    /// left out and why.
    /// </summary>
    Task<List<FolderAclMarkRow>> ListMarkRowsAsync();
}

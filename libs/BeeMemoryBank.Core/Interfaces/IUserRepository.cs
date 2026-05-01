using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByIdAsync(int id);
    Task<List<User>> ListActiveAsync();
    Task<int> CreateAsync(User user);
    Task UpdateAsync(User user);
    Task DeleteAsync(int id, string releasedUsername);
    Task UpdateLastLoginAsync(int id);
    /// <summary>
    /// Updates any tbl_user.key_slot_id == oldSlotId to newSlotId. Used by
    /// KeyManagementService.ChangePasswordAsync (legacy mobile flow) when a slot is rotated
    /// in-place via delete+create — the user→slot FK must follow.
    /// </summary>
    Task RepointKeySlotAsync(int oldSlotId, int newSlotId);
}

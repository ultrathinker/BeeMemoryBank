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

    /// <summary>
    /// Reads the node-local security stamp for a user, IGNORING the is_active filter so a
    /// recently-deleted (IsActive=0) user still resolves — deletion bumps the stamp, so a
    /// pre-deletion cookie's stamp will mismatch and be rejected. Returns null if the user
    /// row does not exist at all.
    /// </summary>
    Task<string?> GetSecurityStampAsync(int id);

    /// <summary>
    /// Regenerates the node-local security stamp (new random value) for a user. Called on
    /// every identity-affecting change to invalidate outstanding Web cookies on next
    /// revalidation. Returns the new stamp.
    /// </summary>
    Task<string> BumpSecurityStampAsync(int id);
}

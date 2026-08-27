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
    /// Assigns a key slot to a user ONLY if they are still an active superadmin with no slot —
    /// the exact precondition <see cref="Models.User"/>-level provisioning checked before the
    /// Argon2id derivation started. Returns false if anything changed underneath (a concurrent
    /// login already provisioned a slot, or an admin demoted/deactivated the user meanwhile),
    /// in which case the caller must delete the slot it just created. Touches only
    /// `key_slot_id`, so it cannot clobber a concurrent password reset or profile edit the way
    /// a whole-row UpdateAsync would.
    /// </summary>
    Task<bool> TryAssignKeySlotAsync(int userId, int slotId);

    /// <summary>
    /// Clears tbl_user.key_slot_id for any user pointing at slotId. Must be called whenever a
    /// slot row is deleted without a replacement — a dangling key_slot_id makes the user look
    /// like they still have a slot, which silently suppresses re-provisioning at next login.
    /// </summary>
    Task ClearKeySlotAsync(int slotId);

    /// <summary>
    /// Ids of the active users holding this role. Used to fan out folder-ACL cache
    /// invalidation when a role's rules change — the cache is keyed per user, so editing one
    /// role has to reach every user that role resolves for. Matching is case-insensitive to
    /// stay consistent with tbl_role.name's COLLATE NOCASE key.
    /// </summary>
    Task<List<int>> GetUserIdsByRoleAsync(string role);

    /// <summary>
    /// Active-user count per role name, for the roles list UI and for the "refuse to delete a
    /// role someone still holds" guard. Roles nobody holds are absent from the dictionary
    /// rather than present with a zero.
    /// </summary>
    Task<Dictionary<string, int>> CountActiveUsersPerRoleAsync();

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

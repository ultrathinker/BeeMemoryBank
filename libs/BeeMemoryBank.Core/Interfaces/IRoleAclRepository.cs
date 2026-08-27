using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Storage for role-level folder rules. Mirrors <see cref="IFolderAclRepository"/> exactly,
/// keyed by role name instead of user id.
/// </summary>
public interface IRoleAclRepository
{
    Task<List<RoleAclEntry>> GetByRoleNameAsync(string roleName);

    Task AddAsync(RoleAclEntry entry);

    /// <summary>Toggles <c>is_read_only</c> on an existing row. Only allow rows carry the flag;
    /// calling this for a deny row is a no-op.</summary>
    Task SetReadOnlyAsync(string roleName, Guid folderId, AclEffect effect, bool isReadOnly);

    /// <summary>Removes both the allow and the deny row for this (role, folder) pair.</summary>
    Task RemoveByRoleAndFolderAsync(string roleName, Guid folderId);

    /// <summary>Distinct roles carrying a rule for this folder. Used by cache invalidation to
    /// walk folder → roles → users when a folder is moved, renamed or deleted.</summary>
    Task<List<string>> GetRoleNamesByFolderIdAsync(Guid folderId);

    /// <summary>Rule count per role name, for the roles list UI. Roles with no rules are
    /// absent from the result rather than present with a zero.</summary>
    Task<Dictionary<string, int>> CountEntriesPerRoleAsync();
}

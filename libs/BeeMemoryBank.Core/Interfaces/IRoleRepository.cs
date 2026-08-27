using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Storage for node-local roles. Business rules (reserved names, system-role protection,
/// refusing to delete a role that users still hold) live in <c>RoleService</c>, not here — this
/// interface is deliberately dumb so the guards sit in one auditable place.
/// </summary>
public interface IRoleRepository
{
    /// <summary>Case-insensitive lookup (<c>tbl_role.name</c> is COLLATE NOCASE).
    /// Returns null when the role does not exist.</summary>
    Task<Role?> GetByNameAsync(string name);

    /// <summary>All roles, system roles first, then alphabetically.</summary>
    Task<List<Role>> ListAsync();

    Task CreateAsync(Role role);

    /// <summary>
    /// Updates the editable metadata only. <see cref="Role.Name"/> is immutable identity: it is
    /// the value stored in every <c>tbl_user.role</c>, and renaming it would need one
    /// transaction spanning two repositories that each open their own connection — a half-applied
    /// rename would leave users pointing at a role that no longer exists, which resolves
    /// fail-closed and locks all of them out. Deleting and recreating is the supported path.
    /// </summary>
    Task UpdateAsync(string name, string displayName, string? description, string basePolicy);

    /// <summary>Deletes the role row (cascading its ACL rows). Returns false if no such role.
    /// Callers must go through <c>RoleService</c>, which refuses system roles and roles that
    /// active users still hold.</summary>
    Task<bool> DeleteAsync(string name);
}

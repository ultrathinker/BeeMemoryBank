namespace BeeMemoryBank.Core.Models;

/// <summary>
/// A node-local role. Two rows are seeded with <see cref="IsSystem"/> = true and cannot be
/// created, renamed or deleted: "superadmin" (bypasses every folder restriction) and "user"
/// (the default non-privileged role). Any other row is a custom role created by a superadmin.
/// <para>
/// Custom roles sit at exactly the same privilege tier as "user": every authorization check in
/// this codebase tests for the literal "superadmin" and treats every other role string as
/// unprivileged, so adding a role can never grant an administrative capability — it only
/// changes which folders the holder sees.
/// </para>
/// <para>
/// Roles are node-local, like <see cref="User"/> and <see cref="FolderAclEntry"/>: created and
/// managed per-node, never propagated through the event stream.
/// </para>
/// </summary>
public class Role
{
    /// <summary>Immutable identity, and the value stored in <c>tbl_user.role</c>. Lower-case
    /// <c>[a-z0-9_-]</c>; never renamed once created (see <c>RoleService</c> for why).</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-facing label. This is the editable one.</summary>
    public string DisplayName { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>True for the two built-in roles. System roles may carry folder rules (that is
    /// the whole point of putting rules on "user"), but cannot be renamed or deleted.</summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// What "this role has no allow rows" means — see <see cref="RoleBasePolicy"/>. Has an
    /// effect ONLY when the role has zero allow rows; a non-empty allow list is a whitelist
    /// under either policy.
    /// </summary>
    public string BasePolicy { get; set; } = RoleBasePolicy.Closed;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Values for <see cref="Role.BasePolicy"/>. This exists so that "no allow rows" has one
/// explicit, admin-visible meaning per role instead of an implicit one that differs between
/// built-in and custom roles.
/// </summary>
public static class RoleBasePolicy
{
    /// <summary>No allow rows ⇒ the whole vault is visible, minus deny rows. The historical
    /// behaviour of the built-in roles.</summary>
    public const string Open = "open";

    /// <summary>No allow rows ⇒ nothing is visible. The default for new custom roles, so a
    /// role assigned before its rules are configured fails closed rather than exposing
    /// everything.</summary>
    public const string Closed = "closed";

    public static bool IsValid(string? value) => value is Open or Closed;
}

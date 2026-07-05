namespace BeeMemoryBank.Core.Models;

// Users are a node-local identity. They are created, authenticated,
// and managed per-node, and are never synchronized to other nodes.
// The primary node hosts all regular users; replica nodes (mobile,
// tablet, backup server) are superadmin-only.
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = UserRoles.User;
    public int? KeySlotId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Node-local flag controlling whether this user may use the AI chat feature;
    /// superadmins bypass this check entirely regardless of its value.</summary>
    public bool ChatAccess { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Node-local security stamp used to revoke stale Web cookies. Bumped on every
    /// identity-affecting change (password/role change, IsActive flip, deletion). The
    /// Web layer embeds the stamp at login as a claim and revalidates it periodically
    /// against this value. NOT synchronized — never put in a sync event payload.
    /// </summary>
    public string SecurityStamp { get; set; } = "";
}

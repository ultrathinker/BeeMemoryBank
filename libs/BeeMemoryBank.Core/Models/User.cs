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
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

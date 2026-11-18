namespace BeeMemoryBank.Core.Models;

// Users are a node-local identity. They are created, authenticated,
// and managed per-node, and are never synchronized to other nodes.
// The primary node hosts all regular users; replica nodes (mobile,
// tablet, backup server) are superadmin-only.
public class User
{
    public int Id { get; set; }
    /// <summary>Stable UUID per local user row (not replicated; used by device ownership metadata).</summary>
    public Guid PersonId { get; set; }

    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = UserRoles.User;
    public int? KeySlotId { get; set; }

    /// <summary>
    /// The hint the person wrote for this password, shown on the sign-in page while the vault is locked (there is no
    /// e-mail recovery). Plain text on purpose - the sign-in page needs it before any key exists - but node-local:
    /// never in a sync payload (tbl_user is not replicated), never in a snapshot's plaintext log, never in audit text
    /// and never in a log line. Null for users created before release 2.7.5 and for internal callers with no person.
    /// </summary>
    public string? PasswordHint { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// <see cref="UserAccountStates.Active"/>, or <see cref="UserAccountStates.Offboarding"/> while a removal process for this person is open
    /// (release 2.12.0): such an account cannot sign in, gets no rights from the API and no new door, and keeps its row until the process closes it.
    /// </summary>
    public string AccountState { get; set; } = UserAccountStates.Active;

    /// <summary>True while a removal process holds this account (see <see cref="AccountState"/>).</summary>
    public bool IsBeingRemoved => AccountState == UserAccountStates.Offboarding;

    /// <summary>Node-local flag controlling whether this user may use the AI chat feature;
    /// superadmins bypass this check entirely regardless of its value.</summary>
    public bool ChatAccess { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// True for a superadmin mirror row replicated from the accounts node via <c>superadmin_doors_set</c>
    /// (release 2.12.0, migration 050). Replicated rows have no local password hash, authenticate by
    /// opening their v2 <c>user</c> door, and are read-only on the shared node.
    /// </summary>
    public bool Replicated { get; set; }

    /// <summary>
    /// Node-local security stamp used to revoke stale Web cookies. Bumped on every
    /// identity-affecting change (password/role change, IsActive flip, deletion). The
    /// Web layer embeds the stamp at login as a claim and revalidates it periodically
    /// against this value. NOT synchronized — never put in a sync event payload.
    /// </summary>
    public string SecurityStamp { get; set; } = "";
}

/// <summary>The values of <c>tbl_user.account_state</c> (migration 051).</summary>
public static class UserAccountStates
{
    public const string Active = "active";
    public const string Offboarding = "offboarding";
}

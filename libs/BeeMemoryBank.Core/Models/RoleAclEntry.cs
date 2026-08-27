namespace BeeMemoryBank.Core.Models;

/// <summary>
/// A folder rule attached to a <see cref="Role"/> rather than to a single user. Semantics are
/// identical to <see cref="FolderAclEntry"/> — the resolver unions role rules with the holder's
/// own per-user rules and then runs the unchanged deny-wins matcher over the result.
/// <para>Node-local, like every other ACL row: never propagated through the event stream.</para>
/// </summary>
public class RoleAclEntry
{
    public string RoleName { get; set; } = "";

    public Guid FolderId { get; set; }

    public AclEffect Effect { get; set; }

    /// <summary>Only meaningful when <see cref="Effect"/> is <see cref="AclEffect.Allow"/>:
    /// true = read-only, false (default) = read+write. Ignored on a deny row.</summary>
    public bool IsReadOnly { get; set; }

    public DateTime CreatedAt { get; set; }
}

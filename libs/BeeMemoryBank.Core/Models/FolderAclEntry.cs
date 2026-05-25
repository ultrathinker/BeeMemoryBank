namespace BeeMemoryBank.Core.Models;

public enum AclEffect
{
    Allow,
    Deny
}

// Folder ACL entries are node-local. They are scoped to a user on
// the node where that user was created, and are not propagated via the
// event stream.
public class FolderAclEntry
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public Guid FolderId { get; set; }
    public AclEffect Effect { get; set; }

    // Only meaningful when Effect == Allow.
    // true = read-only access; false (default) = full read+write.
    // Ignored when Effect == Deny.
    public bool IsReadOnly { get; set; }

    public DateTime CreatedAt { get; set; }
}

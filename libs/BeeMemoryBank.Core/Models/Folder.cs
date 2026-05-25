namespace BeeMemoryBank.Core.Models;

public class Folder
{
    public Guid Id { get; set; }
    public string Path { get; set; } = "/";     // Full path: '/Work/Dev'
    public string Name { get; set; } = "";       // Last segment: 'Dev'
    public string? ParentPath { get; set; }      // '/Work' or null for root-level
    public string Status { get; set; } = "A";   // 'A' active, 'D' deleted
    public long LamportTs { get; set; }
    public Guid? SourceNodeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? CascadeDeleteOpId { get; set; }

    // System folders (e.g. _Drafts) have protected semantics:
    //   - cannot be created with the same name by user-facing endpoints,
    //   - cannot be renamed, moved, or deleted,
    //   - hidden from /api/tree responses when empty.
    // Backend code (e.g. write-fallback for failed remote sync) lazily ensures
    // a system folder via FolderService.EnsureSystemFolderAsync.
    public bool IsSystem { get; set; }

    // Set on rows that mirror content from a remote BMB node. Non-null means
    // "read-only replica": repository write-guards refuse mutations, UI shows
    // the mount-point marker, MCP exposes isRemote: true in bee_get_tree.
    public Guid? RemoteSubscriptionId { get; set; }

    // Original ID of this folder on the owner-node (for resync correlation).
    public string? RemoteOriginId { get; set; }
}

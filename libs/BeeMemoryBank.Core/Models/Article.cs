namespace BeeMemoryBank.Core.Models;

/// <remarks>
/// AUDIT NOTE: Title, TreePath are stored in plaintext by design. This enables
/// tree navigation, search, MCP queries, and folder operations without requiring vault unlock.
/// Article bodies are E2E encrypted (AES-256-GCM, per-article DEK). The trade-off is
/// intentional: metadata privacy vs. usability for a personal knowledge base.
/// </remarks>
public class Article
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string TreePath { get; set; } = "/";
    public Guid? FolderId { get; set; }
    public byte[]? EmbeddingProjection { get; set; }
    public string? EmbeddingModelVersion { get; set; }
    public bool EmbeddingPending { get; set; } = true;
    public string Status { get; set; } = "A";
    public long LamportTs { get; set; }
    public Guid? SourceNodeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Second-layer ("protected") encryption. Protected == true means the body holds a
    // passphrase-encrypted BMBENC1 blob (see ProtectedContentCodec); it is DERIVED from the body
    // content on every write, so it can never desync from what's actually stored. ProtectionHint is
    // an optional plaintext reminder shown on the lock screen before the passphrase is entered.
    public bool Protected { get; set; }
    public string? ProtectionHint { get; set; }

    // Set when this row mirrors an article from a remote BMB node. Non-null
    // makes the row read-only at the repository layer (Phase 4 turns this into
    // write-through-via-REST instead of a hard refusal).
    public Guid? RemoteSubscriptionId { get; set; }
    public string? RemoteOriginId { get; set; }
    public long? RemoteVersion { get; set; }
    public string? RemoteUpdatedBy { get; set; }
}

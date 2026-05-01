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
}

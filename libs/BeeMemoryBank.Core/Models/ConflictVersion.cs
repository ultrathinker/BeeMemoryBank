namespace BeeMemoryBank.Core.Models;

public class ConflictVersion
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }
    public Guid SourceNodeId { get; set; }
    public long LamportTs { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public byte[] IV { get; set; } = [];
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];
    /// <summary>JSON with title, tags, treePath of the losing version for manual conflict recovery.</summary>
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

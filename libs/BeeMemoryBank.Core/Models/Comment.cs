namespace BeeMemoryBank.Core.Models;

public class Comment
{
    public int Id { get; set; }
    public Guid CommentId { get; set; }
    public Guid ArticleId { get; set; }
    public string Text { get; set; } = "";
    public Guid? SourceNodeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public long LamportTs { get; set; }

    // Encryption fields (migration 010)
    public byte[]? Ciphertext { get; set; }
    public byte[]? IV { get; set; }
    public bool Encrypted { get; set; }

    // Soft-delete (migration 006)
    public string? DeletedAt { get; set; }
    public long? DeleteLamportTs { get; set; }
    public Guid? DeleteNodeId { get; set; }
}

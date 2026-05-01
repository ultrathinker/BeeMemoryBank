namespace BeeMemoryBank.Core.Models;

public class Media
{
    public Guid Id { get; set; }
    public Guid? ArticleId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];
    public byte[] IV { get; set; } = [];
    public string Status { get; set; } = "A";
    public long LamportTs { get; set; }
    public Guid? SourceNodeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

namespace BeeMemoryBank.Core.Models;

public class ArticleVersion
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }
    public int VersionNumber { get; set; }
    public string Title { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string TreePath { get; set; } = "";
    public byte[] Ciphertext { get; set; } = [];
    public byte[] IV { get; set; } = [];
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

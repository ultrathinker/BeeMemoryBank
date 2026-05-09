namespace BeeMemoryBank.Core.Models;

public class EncryptedArticleBody
{
    public Guid ArticleId { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public byte[] IV { get; set; } = [];
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];
}

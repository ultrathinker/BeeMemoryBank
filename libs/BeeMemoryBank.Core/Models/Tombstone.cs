namespace BeeMemoryBank.Core.Models;

public class Tombstone
{
    public Guid ArticleId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

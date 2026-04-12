namespace BeeMemoryBank.Core.Models;

public class FolderRestriction
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public int? AgentId { get; set; }
    public Guid FolderId { get; set; }
    public DateTime CreatedAt { get; set; }
}

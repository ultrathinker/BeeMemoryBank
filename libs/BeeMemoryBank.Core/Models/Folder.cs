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
}

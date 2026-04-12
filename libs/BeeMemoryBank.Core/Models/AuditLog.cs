namespace BeeMemoryBank.Core.Models;

public class AuditLog
{
    public int Id { get; set; }
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public string ActorType { get; set; } = "user";
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public DateTime CreatedAt { get; set; }
}

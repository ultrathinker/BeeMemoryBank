namespace BeeMemoryBank.Core.Models;

public class Agent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];
    public string Status { get; set; } = "A";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public long RequestCount { get; set; }
}

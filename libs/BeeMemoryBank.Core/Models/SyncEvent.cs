namespace BeeMemoryBank.Core.Models;

public class SyncEvent
{
    public long SequenceNum { get; set; }
    public Guid EventId { get; set; }
    public Guid NodeId { get; set; }
    public long LamportTs { get; set; }
    public string EventType { get; set; } = "";
    public Guid? ArticleId { get; set; }
    public string Payload { get; set; } = "";
    public byte[] Signature { get; set; } = [];
    public int ProtocolVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }

    // Actor info — not included in signature, synced as metadata
    public string? ActorType { get; set; }
    public string? ActorName { get; set; }
}

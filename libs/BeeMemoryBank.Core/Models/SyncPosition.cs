namespace BeeMemoryBank.Core.Models;

public class SyncPosition
{
    public Guid RemoteNodeId { get; set; }
    public long LastSequenceNum { get; set; }
    public DateTime UpdatedAt { get; set; }
}

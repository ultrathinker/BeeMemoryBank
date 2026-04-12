namespace BeeMemoryBank.Core.Models;

public class SyncPushPosition
{
    public Guid RemoteNodeId { get; set; }
    public long LastPushedSeq { get; set; }
    public DateTime PushedAt { get; set; }
}

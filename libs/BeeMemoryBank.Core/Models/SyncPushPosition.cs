namespace BeeMemoryBank.Core.Models;

public class SyncPushPosition
{
    public Guid RemoteNodeId { get; set; }
    public long LastPushedSeq { get; set; }
    public DateTime PushedAt { get; set; }

    /// <summary>
    /// How far the peer itself says it has read our log, or null if it never said. The delivery
    /// watermark above moves when a page is served, before the peer has applied anything; this one
    /// moves only on the peer's own report, and is what the blind node's log trimmer cuts at
    /// (Codex round 2, security #3).
    /// </summary>
    public long? ReportedSeq { get; set; }
}

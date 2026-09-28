namespace BeeMemoryBank.Sync;

/// <summary>
/// The push-side twin of a 410 (plan 5.1): this node's own log was compacted past what it last
/// pushed to the peer, so the events in between are gone here and pushing on would silently skip
/// them. The PEER needs a snapshot — a blind peer is reseeded (plan 5.2) — while this node itself
/// is fine, which is why the scheduler must not treat it like its own 410.
/// </summary>
public sealed class PushGapException(string remoteUrl, Guid peerNodeId, long pushedUpTo, long lastCompactionCp, long headSeq)
    : SnapshotRequiredException(remoteUrl, lastCompactionCp, headSeq,
        $"Peer {peerNodeId} last received our event {pushedUpTo}, but our log is compacted up to {lastCompactionCp}; " +
        "it can only catch up from a snapshot.")
{
    public Guid PeerNodeId { get; } = peerNodeId;

    public long PushedUpTo { get; } = pushedUpTo;
}

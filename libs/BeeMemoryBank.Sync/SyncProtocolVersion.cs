namespace BeeMemoryBank.Sync;

public static class SyncProtocolVersion
{
    /// <summary>
    /// 1 — original: article/media events embed the full ciphertext as base64.
    /// 2 — events carry ciphertext_sha256 and the bytes move separately through
    ///     /api/sync/blobs/* (see BlobTransport); a node at 1 cannot apply them, and refusing to
    ///     pull from a peer that reports 2 is what keeps it from trying. Events stamped 1 are
    ///     still accepted and applied by a 2 node — the log is full of them.
    /// 3 — blind nodes (plan 3.1): this build refuses events authored by a blind NodeId and never
    ///     seals the master DEK for one. A node at 2 does neither, so it must not learn about a
    ///     blind node at all: it never pulls from a 3 peer (see SyncClient), and a 3 node never
    ///     pushes to it. The event format itself is unchanged, so 1 and 2 events still apply.
    ///     Peers declare this in /api/sync/authenticate and report-position; the receiving node
    ///     keeps the last value per whitelist row for the PC's check before adding a blind node.
    /// </summary>
    public const int Current = 3;

    /// <summary>
    /// The oldest protocol a peer may run and still sync with this build, in either direction.
    /// A peer below 3 would apply a blind node's events and seal the DEK for it (see 3 above), so
    /// it gets no sync token here — which closes every token-gated endpoint to it: events, blobs,
    /// push, report-position, snapshots, restore — and this build does not pull from it either.
    /// A peer that declares no version at all is an old build and is treated the same.
    /// </summary>
    public const int MinPeer = 3;

    public static bool IsCompatiblePeer(int? peerProtocolVersion) => peerProtocolVersion >= MinPeer;

    /// <summary>Event protocol versions this build can apply.</summary>
    public static bool CanApply(int eventProtocolVersion) => eventProtocolVersion is 1 or 2 or 3;
}

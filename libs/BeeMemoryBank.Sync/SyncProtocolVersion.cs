namespace BeeMemoryBank.Sync;

public static class SyncProtocolVersion
{
    /// <summary>
    /// 1 — original: article/media events embed the full ciphertext as base64.
    /// 2 — events carry ciphertext_sha256 and the bytes move separately through
    ///     /api/sync/blobs/* (see BlobTransport); a node at 1 cannot apply them, and refusing to
    ///     pull from a peer that reports 2 is what keeps it from trying. Events stamped 1 are
    ///     still accepted and applied by a 2 node — the log is full of them.
    /// </summary>
    public const int Current = 2;

    /// <summary>Event protocol versions this build can apply.</summary>
    public static bool CanApply(int eventProtocolVersion) => eventProtocolVersion is 1 or 2;
}

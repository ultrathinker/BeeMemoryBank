namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// Why <see cref="EncryptedSegmentStore.LoadAsync"/> could not return usable segment bytes.
/// Purely informational/diagnostic -- every value here maps to exactly the same caller-facing
/// outcome (see <see cref="SegmentLoadResult"/>): "this segment needs to be rebuilt from source
/// article content," never a distinct exception type a caller has to know about and handle
/// individually. Actually triggering that rebuild is a later work package's job (WP-11); this
/// type only makes the "why" cheaply inspectable for logging/diagnostics along the way.
/// </summary>
public enum SegmentRebuildReason
{
    /// <summary>No tbl_search_index_manifest row exists for this segment id.</summary>
    ManifestMissing,

    /// <summary>The manifest row exists but the file it points at is not on disk (or unreadable).</summary>
    FileMissing,

    /// <summary>
    /// The manifest's (or the index key's) recorded dek_epoch does not match the node's current
    /// dek_epoch -- the master DEK rotated since this segment/key was wrapped, so it is no longer
    /// decryptable with today's master DEK. Detected by comparing integers, never by attempting
    /// (and failing) a decrypt.
    /// </summary>
    DekEpochMismatch,

    /// <summary>
    /// The on-disk container's format version does not match what this build understands --
    /// either the manifest's recorded format_version, or the version embedded in the file's own
    /// header (both are checked independently; either mismatching is reported this way).
    /// </summary>
    FormatVersionMismatch,

    /// <summary>
    /// A block failed AES-GCM authentication, or the file/header is otherwise structurally
    /// malformed (including the header's segment id not matching the one requested, i.e. the
    /// whole file was swapped for a different segment's) -- bytes were corrupted or tampered
    /// with after encryption.
    /// </summary>
    CorruptedBlock,
}

/// <summary>
/// Outcome of <see cref="EncryptedSegmentStore.LoadAsync"/>. Deliberately one shape for every
/// failure case in the load path, so a caller written once handles all of them uniformly: check
/// <see cref="Success"/>, and if false, treat it as "needs rebuild" -- <see cref="Reason"/> is
/// available for logging but is not meant to steer different handling per case.
/// </summary>
public sealed class SegmentLoadResult
{
    public bool Success { get; }
    public byte[]? SegmentBytes { get; }
    public SegmentRebuildReason? Reason { get; }

    private SegmentLoadResult(bool success, byte[]? segmentBytes, SegmentRebuildReason? reason)
    {
        Success = success;
        SegmentBytes = segmentBytes;
        Reason = reason;
    }

    public static SegmentLoadResult Ok(byte[] segmentBytes) => new(true, segmentBytes, null);

    public static SegmentLoadResult RebuildNeeded(SegmentRebuildReason reason) => new(false, null, reason);
}

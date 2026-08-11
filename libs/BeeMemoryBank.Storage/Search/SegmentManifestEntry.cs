namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// A row in the local-only tbl_search_index_manifest table: metadata about one on-disk encrypted
/// segment file. This is cache metadata, like everything else in this WP -- never synced, never
/// assumed authoritative. If the row or the file it points at disappears, or its recorded
/// dek_epoch/format_version no longer matches what the node currently understands, the segment is
/// simply rebuilt from source article content (the rebuild trigger itself is a later WP's job).
/// </summary>
public sealed class SegmentManifestEntry
{
    public Guid SegmentId { get; set; }
    public string FilePath { get; set; } = "";
    public int DocCount { get; set; }

    /// <summary>
    /// The node's dek_epoch (see tbl_node_identity) at the moment this segment was encrypted.
    /// Compared against the current epoch on load to cheaply detect "the master DEK rotated
    /// since this segment was written" without attempting a doomed decrypt.
    /// </summary>
    public int DekEpoch { get; set; }

    /// <summary>The on-disk container format version (see EncryptedSegmentFormat.FormatVersion).</summary>
    public int FormatVersion { get; set; }

    public DateTime CreatedAt { get; set; }
}

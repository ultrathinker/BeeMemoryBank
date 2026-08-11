namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// The single row in tbl_search_index_key: this node's "index key" (a random 32-byte secret that
/// actually encrypts segment blocks) wrapped under the master DEK, plus the dek_epoch it was
/// wrapped under. Never synced, never authoritative -- see EncryptedSegmentStore's doc comment
/// for why segment encryption goes through this indirection instead of the master DEK directly.
/// </summary>
public sealed class IndexKeyRow
{
    /// <summary>DekManager.WrapDek's "wrapped" output: a version byte + AES-GCM ciphertext + tag.</summary>
    public byte[] WrappedKey { get; set; } = [];

    public byte[] IV { get; set; } = [];

    /// <summary>The node's dek_epoch at the moment this index key was wrapped.</summary>
    public int DekEpoch { get; set; }

    public DateTime CreatedAt { get; set; }
}

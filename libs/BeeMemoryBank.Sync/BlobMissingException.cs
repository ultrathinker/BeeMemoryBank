namespace BeeMemoryBank.Sync;

/// <summary>
/// An event names a blob by ciphertext_sha256 that this node does not hold. Distinct from a
/// generic apply failure so the sync layer can tell "the transport did not deliver the bytes"
/// (retry: the pusher re-sends whatever the receiver reports missing) from a permanently broken
/// event.
/// </summary>
public sealed class BlobMissingException(string hash)
    : InvalidOperationException($"Referenced blob {hash} is not in the local blob store.")
{
    public string Hash { get; } = hash;
}

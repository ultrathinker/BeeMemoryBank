namespace BeeMemoryBank.Api.Services;

public class SnapshotJoinCache
{
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private CachedEntry? _current;

    /// <param name="dekFingerprint">Null for the plain archive of a node that has not activated metadata encryption; after activation the
    /// fingerprint of the master DEK the archive is sealed under, so an entry never serves a request for another key (a DEK rotation) and a
    /// plain entry never serves an activated node.</param>
    public CachedEntry? TryGet(string? dekFingerprint = null)
    {
        lock (_lock)
        {
            if (_current == null) return null;
            if (DateTime.UtcNow - _current.CreatedAt > TTL || !string.Equals(_current.DekFingerprint, dekFingerprint, StringComparison.Ordinal))
            {
                _current = null;
                return null;
            }
            return _current;
        }
    }

    public void Set(string filePath, string signatureFilePath, long cpSeq, Guid producerNodeId, long lamportTs, string? dekFingerprint = null)
    {
        lock (_lock)
        {
            _current = new CachedEntry(filePath, signatureFilePath, cpSeq, producerNodeId, lamportTs, DateTime.UtcNow, dekFingerprint);
        }
    }

    public void Invalidate()
    {
        lock (_lock) { _current = null; }
    }

    public record CachedEntry(
        string FilePath,
        string SignatureFilePath,
        long CpSeq,
        Guid ProducerNodeId,
        long LamportTs,
        DateTime CreatedAt,
        string? DekFingerprint = null);
}

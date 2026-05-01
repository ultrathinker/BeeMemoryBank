namespace BeeMemoryBank.Api.Services;

public class SnapshotJoinCache
{
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private CachedEntry? _current;

    public CachedEntry? TryGet()
    {
        lock (_lock)
        {
            if (_current == null) return null;
            if (DateTime.UtcNow - _current.CreatedAt > TTL)
            {
                _current = null;
                return null;
            }
            return _current;
        }
    }

    public void Set(string filePath, string signatureFilePath, long cpSeq, Guid producerNodeId, long lamportTs)
    {
        lock (_lock)
        {
            _current = new CachedEntry(filePath, signatureFilePath, cpSeq, producerNodeId, lamportTs, DateTime.UtcNow);
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
        DateTime CreatedAt);
}

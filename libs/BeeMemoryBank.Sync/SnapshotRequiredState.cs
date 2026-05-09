namespace BeeMemoryBank.Sync;

public class SnapshotRequiredState
{
    private volatile SnapshotRequiredException? _lastException;
    public bool IsRequired => _lastException != null;
    public SnapshotRequiredException? LastException => _lastException;

    public void Set(SnapshotRequiredException ex) => _lastException = ex;
    public void Clear() => _lastException = null;
}

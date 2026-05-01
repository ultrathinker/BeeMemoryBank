namespace BeeMemoryBank.Sync;

public class SnapshotRequiredException : Exception
{
    public long LastCompactionCp { get; }
    public long CurrentHeadSeq { get; }
    public string RemoteUrl { get; }

    public SnapshotRequiredException(string remoteUrl, long lastCompactionCp, long currentHeadSeq, string message)
        : base(message)
    {
        RemoteUrl = remoteUrl;
        LastCompactionCp = lastCompactionCp;
        CurrentHeadSeq = currentHeadSeq;
    }
}

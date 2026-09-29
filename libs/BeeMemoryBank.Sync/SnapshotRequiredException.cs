namespace BeeMemoryBank.Sync;

public class SnapshotRequiredException : Exception
{
    /// <summary>The <c>error</c> code of the peer's 410 body that means "your position is older than my log".</summary>
    public const string SequenceTooOldCode = "SEQUENCE_TOO_OLD";

    public long LastCompactionCp { get; }
    public long CurrentHeadSeq { get; }
    public string RemoteUrl { get; }

    /// <summary>The <c>error</c> code the peer's 410 body carried, or null when it carried none (or was no JSON).</summary>
    public string? ErrorCode { get; }

    /// <summary>Whether the 410 body said what its head is, as opposed to leaving <see cref="CurrentHeadSeq"/> at its default.</summary>
    public bool HeadReported { get; }

    public SnapshotRequiredException(string remoteUrl, long lastCompactionCp, long currentHeadSeq, string message,
        string? errorCode = null, bool headReported = true)
        : base(message)
    {
        RemoteUrl = remoteUrl;
        LastCompactionCp = lastCompactionCp;
        CurrentHeadSeq = currentHeadSeq;
        ErrorCode = errorCode;
        HeadReported = headReported;
    }
}

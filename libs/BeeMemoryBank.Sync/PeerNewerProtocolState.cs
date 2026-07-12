namespace BeeMemoryBank.Sync;

public class PeerNewerProtocolState
{
    private volatile bool _hasNewerProtocol;

    public bool HasNewerProtocol
    {
        get => _hasNewerProtocol;
        set => _hasNewerProtocol = value;
    }
}

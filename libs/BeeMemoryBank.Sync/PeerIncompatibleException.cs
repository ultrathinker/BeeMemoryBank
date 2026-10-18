namespace BeeMemoryBank.Sync;

/// <summary>
/// The peer answered, and its software is too old for this node to sync with (BMB-307 fix round: it used to end the session as "nothing to do", and every screen
/// - the phone's heartbeat, "Sync now", the status page - said the sync had gone well). It is a failure of the session like any other: it reaches
/// <see cref="SyncCycleResult.PeerFailures"/>, the contact log and the phone's status, with a sentence a person can act on. No address, key or token in it.
/// </summary>
public sealed class PeerIncompatibleException(int remoteProtocol) : InvalidOperationException(
    $"The peer runs sync protocol {remoteProtocol}, which this node no longer syncs with. Nothing moves either way until it is upgraded.")
{
    public int RemoteProtocol { get; } = remoteProtocol;

    /// <summary>What every screen shows (a closed sentence plus the protocol number).</summary>
    public string Reason => $"it runs an older version (protocol {RemoteProtocol}) than this device syncs with: upgrade it";
}

namespace BeeMemoryBank.Sync;

/// <summary>
/// What the sync loop must be told when a peer on the whitelist is revoked, replaced or given another address (BMB-313, BMB-309, BMB-307): it forgets what
/// it learned about that peer by calling it - the pause of an unreachable peer, the closed window, the failure reason, what the contact record learned at
/// the old address - and tries the new address at once. Implemented by <see cref="SyncScheduler"/>; the writers of the whitelist (the local routes and the
/// applier of replicated whitelist events) call it, and the loop also notices a change by itself at its next cycle.
/// </summary>
public interface IPeerStateResetter
{
    /// <summary>The peer was revoked, replaced or moved: forget what was learned about it, and run a cycle soon.</summary>
    void PeerChanged(Guid nodeId);
}

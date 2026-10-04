namespace BeeMemoryBank.Sync;

/// <summary>
/// The best-effort check that a peer's master-DEK sentinel matches this node's own (a node that holds the master DEK
/// compares them before it pulls). Only a node that holds the master DEK can make it, so the implementation
/// (<c>RemoteSentinelVerifier</c>) is vault code; a node without a master DEK — a blind node — registers none and
/// <see cref="SyncClient"/> then skips the check, exactly as it always did while the session was locked.
/// </summary>
public interface IRemoteSentinelVerifier
{
    /// <summary>
    /// Fetches the peer's sentinel and logs a warning when it does not match; never blocks the pull (an honest peer that
    /// rotated its DEK looks like a mismatch until its COMMIT is applied). Skips silently when this node is locked, when the
    /// peer has no sentinel endpoint, or on a transient failure; an <see cref="InvalidOperationException"/> propagates.
    /// </summary>
    Task VerifyAsync(HttpClient http, string baseUrl, CancellationToken ct);
}

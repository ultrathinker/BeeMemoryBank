using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Reseeds a blind peer when — and only when — it needs it (plan 5.2): the push gap detector fired
/// (<see cref="PushGapException"/>), or the blind node flags itself after a restore_network (5.3).
/// Called by <see cref="SyncScheduler"/> after every sync attempt with a blind peer that reached
/// it. Registered by a full node only; a blind node never reseeds anyone.
/// </summary>
public interface IBlindPeerReseeder
{
    /// <param name="failure">What the sync attempt threw, or null when it succeeded.</param>
    Task AfterSyncAsync(WhitelistEntry peer, HttpClient http, Exception? failure, CancellationToken ct);
}

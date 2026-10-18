namespace BeeMemoryBank.Sync;

/// <summary>
/// Peers that answered a blind node 401 or 403 (BMB-309), and when to ask each again. Unlike <see cref="UnreachablePeers"/> (a peer that does not
/// answer: the pause grows from one cycle to a quarter of an hour) this is a peer that answered and said no - a reverse proxy that lets only some
/// addresses in, a node that revoked this one - so the answer will not change in a minute: one retry an hour, one warning for the whole episode.
/// <para>
/// <b>An episode</b> is one continuous refusal by one peer at one address. It begins with the first 401/403 and it ends - the peer is "open again", one
/// information line says so - when the peer gives any answer that is not a refusal (a good session, another status, a failure of its data), or when what was
/// learned is forgotten: the peer is revoked or replaced, or it is given another address (the refusal came from the old one). A peer that does not answer at
/// all does not end it: nothing is known about its door. A refusal at a new address is a new episode.
/// </para>
/// <para>
/// The memory is bounded by the whitelist (<see cref="Retain"/>) and every change is made under one lock, so that "this refusal opens the episode" is decided
/// exactly once however many sessions report at the same moment.
/// </para>
/// </summary>
public sealed class ClosedPeers
{
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _peers = [];

    private sealed record Entry(string Endpoint, DateTime NextAttemptUtc);

    /// <summary>Peers with an open episode (what the memory holds).</summary>
    public int Count { get { lock (_gate) return _peers.Count; } }

    /// <summary>True while the peer's refusal at <paramref name="endpoint"/> is waiting for its retry. A refusal remembered for another address does not count.</summary>
    public bool ShouldSkip(Guid nodeId, DateTime nowUtc, string? endpoint = null)
    {
        lock (_gate)
            return _peers.TryGetValue(nodeId, out var entry) && entry.Endpoint == (endpoint ?? "") && nowUtc < entry.NextAttemptUtc;
    }

    /// <summary>
    /// Records the refusal; true when it opens an episode (the peer was not closed at this address before), false for a repeat of it. The decision and the
    /// record are one step.
    /// </summary>
    public bool NoteClosed(Guid nodeId, DateTime nowUtc, TimeSpan retry, string? endpoint = null)
    {
        lock (_gate)
        {
            var first = !_peers.TryGetValue(nodeId, out var entry) || entry.Endpoint != (endpoint ?? "");
            _peers[nodeId] = new Entry(endpoint ?? "", nowUtc + retry);
            return first;
        }
    }

    /// <summary>The peer gave an answer that is not a refusal: true when it had been closed (the one line that says it is open again).</summary>
    public bool NoteOpen(Guid nodeId)
    {
        lock (_gate) return _peers.Remove(nodeId);
    }

    /// <summary>Forgets a peer whose address changed or that is gone: the refusal came from another address.</summary>
    public void Forget(Guid nodeId)
    {
        lock (_gate) _peers.Remove(nodeId);
    }

    /// <summary>Keeps only the peers that are on the whitelist now; returns how many entries went (revoked and deleted nodes leave nothing behind).</summary>
    public int Retain(IReadOnlySet<Guid> active)
    {
        lock (_gate)
        {
            var gone = _peers.Keys.Where(id => !active.Contains(id)).ToList();
            foreach (var id in gone) _peers.Remove(id);
            return gone.Count;
        }
    }
}

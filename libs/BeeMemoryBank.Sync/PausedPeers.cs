namespace BeeMemoryBank.Sync;

/// <summary>
/// Blind nodes that answered a push 503 with <c>X-BMB-Paused</c> (<see cref="PeerPausedException"/>): their owner's storage or daily traffic limit stops them. Unlike
/// <see cref="UnreachablePeers"/> (a peer that does not answer: the pause grows from one cycle to a quarter of an hour) and like <see cref="ClosedPeers"/> (a
/// peer that answered and said no) this is a peer that answered, with the time to ask again: nothing grows, one retry at its <c>Retry-After</c> and never later than
/// <see cref="MaxRetry"/>, one log line for the whole episode.
/// <para>
/// <b>An episode</b> is one continuous pause of one peer at one address. It begins with the first paused answer and ends - the peer is "open again", one information
/// line says so - when the peer gives any answer that is not a pause (a good session, a refusal, another status), or when what was learned is forgotten: the peer is
/// revoked or replaced, or it is given another address. A peer that does not answer at all does not end it. A change of the reason inside an episode (the day's traffic
/// ran out, then the storage) is the same episode: the pause goes on, only its words change.
/// </para>
/// <para>
/// The retry is at most an hour even when the node says "until midnight": a retry is one handshake and one refused request, and an owner who raised the limit in the
/// console is served within the hour instead of at the next midnight. Every change is made under one lock, so that "this answer opens the episode" is decided exactly
/// once however many sessions report at the same moment; the memory is bounded by the whitelist (<see cref="Retain"/>).
/// </para>
/// </summary>
public sealed class PausedPeers
{
    /// <summary>The longest a paused peer is left alone: the hourly retry of a pause that names no (or a longer) time.</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(1);

    /// <summary>The shortest: a peer that says "now" or nothing sensible is not asked again within this time, whatever the loop's interval.</summary>
    public static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _peers = [];

    /// <summary>The shortest wait (<see cref="MinRetry"/> unless a test says otherwise).</summary>
    public TimeSpan MinWait { get; set; } = MinRetry;

    private sealed record Entry(string Endpoint, string Reason, DateTime NextAttemptUtc);

    /// <summary>The peers with an open episode (what the memory holds).</summary>
    public int Count { get { lock (_gate) return _peers.Count; } }

    /// <summary>True while the peer's pause at <paramref name="endpoint"/> is waiting for its retry. A pause remembered for another address does not count.</summary>
    public bool ShouldSkip(Guid nodeId, DateTime nowUtc, string? endpoint = null)
    {
        lock (_gate)
            return _peers.TryGetValue(nodeId, out var entry) && entry.Endpoint == (endpoint ?? "") && nowUtc < entry.NextAttemptUtc;
    }

    /// <summary>The reason of the pause the peer is in, or null when it is in none (the closed token of <see cref="PeerPauseReport"/>).</summary>
    public string? ReasonOf(Guid nodeId)
    {
        lock (_gate) return _peers.TryGetValue(nodeId, out var entry) ? entry.Reason : null;
    }

    /// <summary>
    /// Records the pause and when to ask again (<paramref name="retry"/>, kept within <see cref="MinRetry"/> and <see cref="MaxRetry"/>); true when it opens an episode
    /// (the peer was not paused at this address before), false for a repeat of it. The decision and the record are one step.
    /// </summary>
    public bool NotePaused(Guid nodeId, DateTime nowUtc, TimeSpan retry, string reason, string? endpoint = null)
    {
        var wait = retry < MinWait ? MinWait : retry > MaxRetry ? MaxRetry : retry;
        lock (_gate)
        {
            var first = !_peers.TryGetValue(nodeId, out var entry) || entry.Endpoint != (endpoint ?? "");
            _peers[nodeId] = new Entry(endpoint ?? "", reason, nowUtc + wait);
            return first;
        }
    }

    /// <summary>The peer gave an answer that is not a pause: true when it had been paused (the one line that says it goes on).</summary>
    public bool NoteOpen(Guid nodeId)
    {
        lock (_gate) return _peers.Remove(nodeId);
    }

    /// <summary>Forgets a peer whose address changed or that is gone: the pause came from another address.</summary>
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

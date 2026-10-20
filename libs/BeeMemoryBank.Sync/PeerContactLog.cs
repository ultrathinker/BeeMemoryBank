using System.Collections.Concurrent;
using System.Net;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// When this node last made contact with each peer, in either direction, and how it is going (BMB-313). "Contact" is any completed session, whether
/// or not events moved: <b>we -&gt; it</b> (this node's loop called the peer and the session ended well) or <b>it -&gt; us</b> (the peer authenticated to
/// this node, or called it with a token it was given). Kept apart from the position tables, which say how far events went: the last push of events
/// grows old on a quiet network although the peers talk every minute, and a peer that has no address and only calls in has no pull position at all.
///
/// <para>In memory, per process, and honest about it: a restart empties it, and until this process has called a peer (the first cycle comes five seconds
/// after the start) the status says "unknown" for that peer, not what an older process saw (<c>PeerContactView</c>). What this node calls is remembered
/// per ENDPOINT: when the address of a peer changes, what was learned at the old address (an answer, a failure streak, a reason) is not the new address's
/// and is not shown for it. The log is bounded: <see cref="Reconcile"/> drops the peers that are no longer on the whitelist and those nobody has touched for
/// <see cref="Retention"/>.</para>
/// </summary>
public sealed class PeerContactLog
{
    /// <summary>A gap this long between two failed attempts means this computer was asleep in between: the streak starts again (as <see cref="UnreachablePeers.StreakBreak"/>).</summary>
    public static readonly TimeSpan StreakBreak = UnreachablePeers.StreakBreak;

    /// <summary>When this process began: what "since this node started" means.</summary>
    public static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>A peer nothing has been noted about for this long is dropped: a node that was removed or replaced and never came back is not remembered for the life of the process.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<Guid, Contact> _peers = new();

    /// <summary>What is known about one peer; every field is null until it happened in this process.</summary>
    /// <param name="OutboundOkAt">The last session this node called and that ended well (we -&gt; it).</param>
    /// <param name="InboundAt">The last time the peer called this node (it -&gt; us).</param>
    /// <param name="OutboundFailingSince">The start of the streak of failed calls, or null while the last call was good.</param>
    /// <param name="OutboundFailure">Why the last call failed, in a short sentence from a closed set (<see cref="Describe"/>).</param>
    /// <param name="OutboundFailureAt">When the last call failed.</param>
    /// <param name="OutboundEndpoint">The address the outbound facts were learned at (<see cref="EndpointKey"/>); null when the caller did not say.</param>
    /// <param name="Platform">What the peer runs on (<see cref="Metadata.NodePlatform"/>), as it told this process; null until it did (a build before 2.6.4 never does).</param>
    /// <param name="InContainer">The peer said it runs in a container; null until it said anything.</param>
    /// <param name="InboundFrom">The address the peer's last call came from, as this node's server saw it (behind a proxy that is the proxy).</param>
    public sealed record Contact(
        DateTime? OutboundOkAt = null, DateTime? InboundAt = null, DateTime? OutboundFailingSince = null,
        string? OutboundFailure = null, DateTime? OutboundFailureAt = null, string? OutboundEndpoint = null,
        string? Platform = null, bool? InContainer = null, string? InboundFrom = null)
    {
        /// <summary>The newest thing noted, for the retention rule.</summary>
        internal DateTime? Newest => new[] { OutboundOkAt, InboundAt, OutboundFailureAt }.Max();
    }

    /// <summary>The key of an address for the purpose of "is this the same place": trimmed, without a trailing slash, lower case.</summary>
    public static string? EndpointKey(string? address) =>
        string.IsNullOrWhiteSpace(address) ? null : address.Trim().TrimEnd('/').ToLowerInvariant();

    private static Contact ForEndpoint(Contact c, string? endpoint) =>
        endpoint is null || c.OutboundEndpoint is null || string.Equals(c.OutboundEndpoint, endpoint, StringComparison.Ordinal)
            ? c
            : c with { OutboundOkAt = null, OutboundFailingSince = null, OutboundFailure = null, OutboundFailureAt = null, OutboundEndpoint = null };

    public void NoteOutboundOk(Guid nodeId, DateTime nowUtc, string? endpoint = null)
    {
        var key = EndpointKey(endpoint);
        _peers.AddOrUpdate(nodeId,
            _ => new Contact(OutboundOkAt: nowUtc, OutboundEndpoint: key),
            (_, c) => ForEndpoint(c, key) with { OutboundOkAt = nowUtc, OutboundFailingSince = null, OutboundFailure = null, OutboundFailureAt = null, OutboundEndpoint = key });
    }

    public void NoteOutboundFailure(Guid nodeId, DateTime nowUtc, string reason, string? endpoint = null)
    {
        var key = EndpointKey(endpoint);
        _peers.AddOrUpdate(nodeId,
            _ => new Contact(OutboundFailingSince: nowUtc, OutboundFailure: reason, OutboundFailureAt: nowUtc, OutboundEndpoint: key),
            (_, c) =>
            {
                var same = ForEndpoint(c, key);
                return same with
                {
                    OutboundFailingSince = same.OutboundFailingSince is { } since && same.OutboundFailureAt is { } last && nowUtc - last < StreakBreak ? since : nowUtc,
                    OutboundFailure = reason,
                    OutboundFailureAt = nowUtc,
                    OutboundEndpoint = key,
                };
            });
    }

    /// <param name="from">Where the call came from; kept from the last call that said, so a call that does not say leaves what an earlier one did.</param>
    public void NoteInbound(Guid nodeId, DateTime nowUtc, string? from = null) =>
        _peers.AddOrUpdate(nodeId, _ => new Contact(InboundAt: nowUtc, InboundFrom: from), (_, c) => c with { InboundAt = nowUtc, InboundFrom = from ?? c.InboundFrom });

    /// <summary>
    /// What a peer said it runs on (in its identity, or when it called this node). Not a contact and not an endpoint's fact: it stays through an address change and a
    /// failure streak. A peer that says nothing (a build before 2.6.4) leaves what is known as it is.
    /// </summary>
    public void NotePlatform(Guid nodeId, string? platform, bool? inContainer)
    {
        if (platform is null && inContainer is null) return;
        _peers.AddOrUpdate(nodeId,
            _ => new Contact(Platform: platform, InContainer: inContainer),
            (_, c) => c with { Platform = platform ?? c.Platform, InContainer = inContainer ?? c.InContainer });
    }

    /// <summary>
    /// What is known about a peer. With <paramref name="endpoint"/> (the address the whitelist has for it now) what was learned at ANOTHER address is left out:
    /// a failure streak or an answer of the old address is never shown for the new one.
    /// </summary>
    public Contact Get(Guid nodeId, string? endpoint = null) =>
        _peers.TryGetValue(nodeId, out var contact) ? ForEndpoint(contact, EndpointKey(endpoint)) : new Contact();

    /// <summary>Forgets a peer entirely (it was revoked or removed, or a new node took its place).</summary>
    public void Forget(Guid nodeId) => _peers.TryRemove(nodeId, out _);

    /// <summary>Forgets what this node learned by calling the peer (its address changed): the inbound record, which the peer's own calls made, stays.</summary>
    public void ForgetOutbound(Guid nodeId) =>
        _peers.AddOrUpdate(nodeId, _ => new Contact(), (_, c) => c with { OutboundOkAt = null, OutboundFailingSince = null, OutboundFailure = null, OutboundFailureAt = null, OutboundEndpoint = null });

    /// <summary>Keeps the peers that are on the whitelist and have been touched within <see cref="Retention"/>; drops the rest.</summary>
    public void Reconcile(IReadOnlySet<Guid> active, DateTime nowUtc)
    {
        foreach (var (id, contact) in _peers)
            if (!active.Contains(id) || (contact.Newest is { } newest && nowUtc - newest > Retention))
                _peers.TryRemove(id, out _);
    }

    /// <summary>How many peers are remembered (for the bound's test).</summary>
    public int Count => _peers.Count;

    /// <summary>
    /// What went wrong with a call, in a sentence a person can act on (the reason that "not answering since ..." gives). A CLOSED set: nothing of the
    /// exception's own text is put in (an address, a key, a token, a path, a parser's message), only what kind of failure it was; the details are in this
    /// node's log, where the scheduler wrote them.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        NoSyncServerException n => $"{NoSyncServerException.Headline}: {n.Reason}",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } h =>
            $"it answered {(int)h.StatusCode}: its door is closed to this node (a proxy that does not let this address in, or it no longer knows this node)",
        HttpRequestException { StatusCode: { } code } => $"it answered {(int)code} {code}",
        HttpRequestException h when PeerSocketConnector.IsRefusal(h) =>
            "no connection was made: the name leads to an address this node does not call (an unspecified, link-local or cloud-metadata address)",
        HttpRequestException h => "no answer" + Cause(h),
        TaskCanceledException or TimeoutException => "no answer: it did not answer in time",
        OperationCanceledException => "the call was cancelled",
        PeerIdentityMismatchException => "another node answers at this address than the one this node has on record for it (a stale address, or something else is there)",
        PeerKeyMismatchException => "the node at this address has the right id but another key than the one this network knows it by: it is not trusted",
        PeerIncompatibleException p => p.Reason,
        PushGapException => "it missed events this node's compaction removed: it has to rejoin",
        SnapshotRequiredException => "it has compacted past this node: this node needs a new snapshot of it",
        System.Security.Authentication.AuthenticationException => "no answer: its certificate was not accepted",
        IOException => "the connection broke while this node was talking to it",
        System.Text.Json.JsonException => "it sent an answer this node cannot read (is it a sync server of the same version?)",
        FormatException or InvalidDataException or System.Security.Cryptography.CryptographicException =>
            "it sent something this node cannot read or verify (a damaged or foreign answer)",
        _ => "the call failed (the details are in this node's log)",
    };

    private static string Cause(HttpRequestException ex) => ex.InnerException switch
    {
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused } => ": connection refused (nothing listens at that address)",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut } => ": it did not answer in time",
        TimeoutException => ": it did not answer in time",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.HostDown } =>
            ": the address cannot be reached",
        System.Security.Authentication.AuthenticationException => ": its certificate was not accepted",
        _ => "",
    };
}

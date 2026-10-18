namespace BeeMemoryBank.Api.Models;

/// <summary>Returned in response to /api/sync/challenge.</summary>
public record SyncChallengeResponse(string Challenge, Guid ServerNodeId);

/// <summary>Node authentication request.</summary>
/// <param name="ProtocolVersion">
/// The protocol the caller declares (ISyncProtocolPolicy.Declared: 3, or 4 once it applied the metadata activation). Absent
/// (null) from builds before protocol 3. Recorded on the caller's whitelist row (WhitelistEntry.LastProtocolVersion); one this
/// node does not accept (below SyncProtocolVersion.MinPeer, or after activation below 4 without the capability), or absent,
/// gets no token (426).
/// </param>
/// <param name="Capabilities">What the caller's build advertises (docs/design/step-a-spec.md 5.1); absent from 2.5 builds.</param>
/// <param name="AppVersion">The caller's application version; absent from 2.5 builds.</param>
public record SyncAuthRequest(Guid NodeId, string ChallengeB64, string SignatureB64, int? ProtocolVersion = null,
    IReadOnlyList<string>? Capabilities = null, string? AppVersion = null);

/// <summary>Response after successful authentication.</summary>
public record SyncAuthResponse(string Token);

/// <summary>Identity of this node for remote nodes.</summary>
/// <param name="ProtocolVersion">What this node declares: 3, or 4 once it applied the metadata activation.</param>
/// <param name="Capabilities">What this build advertises (docs/design/step-a-spec.md 5.1); older nodes ignore it.</param>
public record SyncIdentityResponse(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion,
    IReadOnlyList<string>? Capabilities = null, string? AppVersion = null);

/// <summary>Result of applying events.</summary>
/// <param name="Applied">Count of events successfully persisted via ApplyAsync.</param>
/// <param name="Skipped">Count of events rejected (signature, schema, replay shield, etc.).</param>
/// <param name="LastAppliedSequence">
/// Sequence_num of the last event in the contiguous batch prefix that was successfully applied or
/// deliberately dropped. It never advances past a skipped event, even if later events in the same
/// request are accepted.
/// The pusher MUST use this — not batch[^1].SequenceNum — when advancing its push cursor.
/// Otherwise events that the remote skipped get permanently lost: the cursor steps over
/// them and the pusher will never re-send. null means the accepted prefix is empty (all-skipped,
/// a first-item skip, or an empty batch); pusher should leave its cursor unchanged.
/// </param>
/// <param name="Held">Step A (docs/design/step-a-spec.md 6.2), informative: how many of <paramref name="Applied"/> were stored
/// held (a locked node, a key not here yet). They count as applied, so a pusher of any version advances over them.</param>
public record SyncApplyResult(int Applied, int Skipped, long? LastAppliedSequence = null, int Dropped = 0, int Held = 0);

// ─── Blob transport (protocol 2) ───────────────────────────────────────────────
// Wire shapes for /api/sync/blobs/*. The client side (BlobTransport in BeeMemoryBank.Sync) keeps
// its own private copies of these — the Sync library does not reference Api — so a change here
// must be mirrored there.

/// <summary>Request body for /api/sync/blobs/check and /api/sync/blobs/get.</summary>
public record SyncBlobHashList(List<string> Hashes);

/// <summary>Response of /api/sync/blobs/check: the requested hashes this node does NOT hold.</summary>
public record SyncBlobMissing(List<string> Missing);

/// <summary>One blob on the wire: lowercase-hex SHA-256 and base64 ciphertext.</summary>
public record SyncBlob(string Hash, string Data);

/// <summary>Request body of POST /api/sync/blobs and response of /api/sync/blobs/get.</summary>
public record SyncBlobBatch(List<SyncBlob> Blobs);

/// <summary>
/// Response of POST /api/sync/blobs. Rejected counts items whose base64 was malformed, that were
/// empty, or whose bytes do not hash to the claimed hash — those are not stored at all.
/// </summary>
public record SyncBlobStoreResult(int Stored, int Rejected);

/// <summary>
/// One peer in the header sync status (BMB-313). <c>LastContactAt</c> is the last successful CONTACT with it - any completed session, either way,
/// whether or not events moved - and <c>LastContactDirection</c> says whose call it was; it used to be the time of the last PUSH of events, which grows
/// old on a quiet network while the peers talk every minute. The last push of data has its own field, <c>LastPushAt</c>.
/// </summary>
/// <param name="LastContactDirection"><c>outbound</c> (we -&gt; it: this node called) or <c>inbound</c> (it -&gt; us: it called this node); null when no contact is known.</param>
/// <param name="LastPushAt">The last time events were delivered to it (this node's push, or its pull of them).</param>
/// <param name="LastOutboundAt">The last call of this node that ended well.</param>
/// <param name="LastInboundAt">The last time it called this node.</param>
/// <param name="CanBeCalled">It has an address: this node calls it. Without one (a phone, a node behind a NAT) it calls this node.</param>
/// <param name="State"><c>answering</c>, <c>not_answering</c> (this node calls it and it does not answer), <c>calls_us</c> (no address: it calls in) or <c>unknown</c> (nothing heard since this node started).</param>
/// <param name="Online">Contact within <see cref="PeerContactView.OnlineWindowFor"/> of now (a window made from the effective sync interval, not a fixed five minutes), and not failing.</param>
/// <param name="NotAnsweringSince">When this node's calls to it began to fail, while nothing has been heard from it since.</param>
/// <param name="NotAnsweringReason">Why, in a sentence.</param>
public record DeliveryNodeStatus(
    Guid NodeId,
    string DisplayName,
    string NodeType,
    long LastPushedSeq,
    long HeadSeq,
    int UnsyncedCount,
    bool IsSynced,
    DateTime? LastContactAt,
    string? LastContactDirection = null,
    DateTime? LastPushAt = null,
    DateTime? LastOutboundAt = null,
    DateTime? LastInboundAt = null,
    bool CanBeCalled = false,
    string State = PeerContactView.Unknown,
    bool Online = false,
    DateTime? NotAnsweringSince = null,
    string? NotAnsweringReason = null);

/// <summary>The answer of <c>POST /api/sync/nodes/{id}/check</c>: what happened when this node asked the peer now.</summary>
/// <param name="Outcome"><c>answered</c>, <c>not_answering</c>, <c>calls_us</c> (no address: it calls this node, nothing was asked), <c>busy</c> (a cycle is running), <c>invisible</c>, <c>unknown_node</c>.</param>
public record NodeCheckResponse(string Outcome, string? Reason, int Applied, DeliveryNodeStatus? Node);

/// <summary>Turns what this node knows about a peer into the fields of <see cref="DeliveryNodeStatus"/>; one place, so the header, the Admin page and the check agree.</summary>
public static class PeerContactView
{
    public const string Answering = "answering";
    public const string NotAnswering = "not_answering";
    public const string CallsUs = "calls_us";
    public const string Unknown = "unknown";

    /// <summary>The interval of a sync loop that was not told another (<c>BMB_SYNC_INTERVAL_SECONDS</c> sets it).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>How often a phone calls in the background (Android's periodic sync): a peer that calls this node is as quiet as its own cadence, whatever this node's interval.</summary>
    public static readonly TimeSpan MobileCadence = TimeSpan.FromMinutes(5);

    /// <summary>How many expected cycles may pass without contact before a peer is no longer called online: two missed ones and half of a third.</summary>
    public const double GraceFactor = 2.5;

    /// <summary>The shortest window, so that a very short interval does not make a peer flicker.</summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(2);

    /// <summary>How far ahead of this clock a recorded time may be before it is not believed: a peer's or an older process's clock, a clock that was set back.</summary>
    public static readonly TimeSpan SkewTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A peer heard from within this time, and not failing, is online: <see cref="GraceFactor"/> times the cadence it is expected at - this node's own sync
    /// interval for a peer this node calls, the longer of that and <see cref="MobileCadence"/> for a peer that calls in - and never less than
    /// <see cref="MinimumWindow"/>. With the default 60 s that is 2.5 minutes for a server and 12.5 minutes for a phone; with a 600 s interval, 25 minutes.
    /// </summary>
    public static TimeSpan OnlineWindowFor(TimeSpan? syncInterval, bool callsUs)
    {
        var own = syncInterval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
        var cadence = callsUs && own < MobileCadence ? MobileCadence : own;
        var window = TimeSpan.FromTicks((long)(cadence.Ticks * GraceFactor));
        return window < MinimumWindow ? MinimumWindow : window;
    }

    /// <param name="memory">What this process saw (<see cref="BeeMemoryBank.Sync.PeerContactLog"/>).</param>
    /// <param name="pulledAt">When this node last finished a pull from it (a persisted trace of an outbound session).</param>
    /// <param name="push">The row of events delivered to it, if any.</param>
    /// <param name="syncInterval">The effective interval of this node's sync loop (<c>SyncScheduler.Interval</c>); null means <see cref="DefaultInterval"/>.</param>
    public static DeliveryNodeStatus Build(
        BeeMemoryBank.Core.Models.WhitelistEntry node, BeeMemoryBank.Sync.PeerContactLog.Contact memory, DateTime? pulledAt,
        BeeMemoryBank.Core.Models.SyncPushPosition? push, long headSeq, int unsyncedCount, DateTime nowUtc, TimeSpan? syncInterval = null)
    {
        var canBeCalled = !string.IsNullOrEmpty(node.ApiAddress);
        var blind = BeeMemoryBank.Core.Models.BlindNodeId.IsBlind(node.NodeId);

        // Outbound: this process's own record, else the position the last finished pull left on disk.
        var outbound = Latest(Believed(memory.OutboundOkAt, nowUtc), Believed(pulledAt, nowUtc));
        // Inbound: this process's record, else the persisted trace of the peer's last authentication. For a blind node this node calls, that column is
        // also stamped by this node's own my-standing call, so it is no evidence of the peer calling in: an outbound timestamp is never promoted to inbound.
        var inbound = Latest(Believed(memory.InboundAt, nowUtc), blind && canBeCalled ? null : Believed(node.LastProtocolSeenAt, nowUtc));
        var last = Latest(outbound, inbound);
        string? direction = last is null ? null : inbound is { } i && (outbound is null || i > outbound) ? "inbound" : "outbound";

        // "Not answering": this node calls it, the last call failed, and nothing at all has been heard from it since the streak began.
        DateTime? notSince = null;
        string? reason = null;
        if (canBeCalled && memory.OutboundFailingSince is { } since && (inbound is null || inbound < since))
        {
            notSince = Utc(since);
            reason = memory.OutboundFailure;
        }

        // This process keeps its record of calls in memory: until it has called the peer once (or tried), it knows nothing about how the calls go, and what
        // the positions on disk say is the last time an earlier process finished a pull. That is shown as the last contact, and the state is "unknown" until
        // the next cycle or the next check says (never an invented "answering").
        var checkedHere = memory.OutboundOkAt is not null || memory.OutboundFailingSince is not null;
        var state = notSince is not null ? NotAnswering : !canBeCalled ? CallsUs : last is null || !checkedHere ? Unknown : Answering;
        var online = state != NotAnswering && last is { } at && nowUtc - at < OnlineWindowFor(syncInterval, !canBeCalled);
        return new DeliveryNodeStatus(
            node.NodeId, node.DisplayName, canBeCalled ? "public" : "private", push?.LastPushedSeq ?? 0, headSeq, unsyncedCount, unsyncedCount == 0,
            last, direction, Utc(push?.PushedAt), outbound, inbound, canBeCalled, state, online, notSince, reason);
    }

    private static DateTime? Utc(DateTime? time) =>
        time is null || time.Value == default || time.Value.Year < 2000 ? null : DateTime.SpecifyKind(time.Value, DateTimeKind.Utc);

    /// <summary>
    /// A recorded time as UTC and as far as it can be believed: pre-2000 and default times are "never", a time a little ahead of this clock (<see cref="SkewTolerance"/>)
    /// is taken as now, and one further ahead is not a contact at all (a clock that was set back, a peer with a wrong clock) and is left out.
    /// </summary>
    private static DateTime? Believed(DateTime? time, DateTime nowUtc)
    {
        if (Utc(time) is not { } utc) return null;
        if (utc <= nowUtc) return utc;
        return utc - nowUtc <= SkewTolerance ? nowUtc : null;
    }

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}

public record DeliveryStatusResponse(
    Guid? LocalNodeId,
    bool IsInvisible,
    List<DeliveryNodeStatus> Nodes);

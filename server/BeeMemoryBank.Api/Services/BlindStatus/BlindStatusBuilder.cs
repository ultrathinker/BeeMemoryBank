using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Services.BlindStatus;

/// <summary>
/// One peer row of <c>GET /api/blind/status</c> (<c>peers[]</c>). Field names are fixed by the
/// blind-nodes contract (CONTRACTS §5) and serialized snake_case on purpose: the same JSON is read
/// by the blind console page, the Windows app and the CLI, which must keep agreeing on it across
/// releases — see the JsonPropertyName attributes rather than any serializer policy.
/// </summary>
public sealed record BlindPeerStatus(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("name")] string Name,
    /// <summary>The later of the last pull from the peer and the last push of events to it: the DELIVERY time this field has always been (BMB-313 keeps its meaning).</summary>
    [property: JsonPropertyName("last_contact")] DateTime? LastContact,
    /// <summary>Events this peer has not pulled from us yet; null when we have never seen it sync.</summary>
    [property: JsonPropertyName("lag")] long? Lag,
    /// <summary>The last completed session with the peer, either way, whether or not events moved: the same projection as the sync status of the full node (BMB-313).</summary>
    [property: JsonPropertyName("contact_at")] DateTime? ContactAt = null,
    /// <summary><c>outbound</c> (this node called), <c>inbound</c> (the peer called), or null.</summary>
    [property: JsonPropertyName("contact_direction")] string? ContactDirection = null,
    [property: JsonPropertyName("last_push_at")] DateTime? LastPushAt = null,
    [property: JsonPropertyName("last_outbound_at")] DateTime? LastOutboundAt = null,
    [property: JsonPropertyName("last_inbound_at")] DateTime? LastInboundAt = null,
    /// <summary><c>answering</c>, <c>not_answering</c>, <c>calls_us</c> or <c>unknown</c> (not checked since this node started).</summary>
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("online")] bool? Online = null,
    [property: JsonPropertyName("not_answering_since")] DateTime? NotAnsweringSince = null,
    [property: JsonPropertyName("not_answering_reason")] string? NotAnsweringReason = null);

/// <summary>
/// One job row of <c>GET /api/blind/status</c> (<c>jobs[]</c>): a backup, verify or wipe the node
/// is running or has run recently. <c>progress</c> is 0..1 (null when the job cannot know it),
/// <c>eta</c> an ISO timestamp estimate, <c>mode</c> the CPU mode it runs under (plan §8).
/// </summary>
public sealed record BlindJobStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("progress")] double? Progress,
    [property: JsonPropertyName("speed_bytes_per_sec")] long? SpeedBytesPerSec,
    [property: JsonPropertyName("eta")] DateTime? Eta,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("finished_at")] DateTime? FinishedAt);

/// <summary>
/// Accumulates the JSON of <c>GET /api/blind/status</c> (plan §9, CONTRACTS §5). One instance per
/// request: the endpoint runs every registered <see cref="IBlindStatusContributor"/> against the
/// same builder, then serializes <see cref="Build"/>. The base fields (version, protocol, node id,
/// role, peers, paths, storage) are filled by <see cref="CoreBlindStatusContributor"/>; other
/// agents own their sections — <c>anchor</c> and <c>boxes</c> (recovery), <c>pairing</c> — and add
/// them through <see cref="Set"/> without touching this file.
///
/// <para>Top-level fields are a plain name→value map serialized as-is (dictionary keys are not
/// subject to the camelCase naming policy, so the contracted snake_case names survive whatever
/// options the host configures); only the repeated rows (<c>peers</c>, <c>jobs</c>) are typed
/// records, so a contributor cannot append a malformed row.</para>
/// </summary>
public sealed class BlindStatusBuilder
{
    private readonly SortedDictionary<string, object?> _fields = new(StringComparer.Ordinal);
    private readonly List<BlindPeerStatus> _peers = [];
    private readonly List<BlindJobStatus> _jobs = [];

    /// <summary>Sets (or replaces) one top-level field or section — e.g. "role", "anchor", "pairing".</summary>
    public void Set(string name, object? value) => _fields[name] = value;

    public void AddPeer(BlindPeerStatus peer) => _peers.Add(peer);

    public void AddJob(BlindJobStatus job) => _jobs.Add(job);

    /// <summary>The response object: every section set so far, with <c>peers</c> and <c>jobs</c> guaranteed present.</summary>
    public IReadOnlyDictionary<string, object?> Build()
    {
        _fields["peers"] = _peers;
        _fields["jobs"] = _jobs;
        return _fields;
    }
}
